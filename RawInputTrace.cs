using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Diagnostic-only Win32 input recorder. Records WM_MOUSEMOVE messages, direct
/// raw-input reads, cursor sampling/warping, device registration, and XInput.
/// </summary>
internal sealed unsafe class RawInputTrace : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint WmMouseMove = 0x0200;
    private const uint PmRemove = 0x0001;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RawInputHeaderSizeX64 = 24;

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly TraceSlot[] _slots;
    private readonly StreamWriter _writer;
    private readonly Timer _flushTimer;
    private readonly object _writerLock = new();
    private readonly IHook<PeekMessageWDelegate> _peekMessageHook;
    private readonly IHook<RegisterRawInputDevicesDelegate> _registerHook;
    private readonly IHook<GetRawInputDataDelegate> _getRawInputDataHook;
    private readonly IHook<GetCursorPosDelegate> _getCursorPosHook;
    private readonly IHook<SetCursorPosDelegate> _setCursorPosHook;
    private readonly IHook<XInputGetStateDelegate>? _xInputGetStateHook;

    private int _reserved;
    private int _read;
    private int _dropped;
    private bool _disposed;

    public RawInputTrace(ModContext context)
    {
        _logger = context.Logger;
        int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
        _slots = new TraceSlot[capacity];

        string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
        string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, $"raw-input-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        _writer = new StreamWriter(new FileStream(
            tracePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan));
        _writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        _writer.WriteLine($"# process_start_utc={Process.GetCurrentProcess().StartTime.ToUniversalTime():O}");
        _writer.WriteLine("sequence,kind,qpc_enter,qpc_exit,os_thread,remove_flags,result,raw_type,raw_size,mouse_flags,x,y,usage_page,usage,device_flags,target,user_index,packet,buttons,left_trigger,right_trigger,left_x,left_y,right_x,right_y,message,message_time");
        _writer.Flush();

        nint user32 = Native.GetModuleHandleW("user32.dll");
        if (user32 == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandleW(user32.dll) failed");
        nint peekAddress = Native.GetProcAddress(user32, "PeekMessageW");
        nint registerAddress = Native.GetProcAddress(user32, "RegisterRawInputDevices");
        nint rawDataAddress = Native.GetProcAddress(user32, "GetRawInputData");
        nint getCursorAddress = Native.GetProcAddress(user32, "GetCursorPos");
        nint setCursorAddress = Native.GetProcAddress(user32, "SetCursorPos");
        if (peekAddress == 0 || registerAddress == 0 || rawDataAddress == 0 || getCursorAddress == 0 || setCursorAddress == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve Win32 input exports");

        _peekMessageHook = context.Hooks!.CreateHook<PeekMessageWDelegate>(PeekMessageW, peekAddress);
        _registerHook = context.Hooks!.CreateHook<RegisterRawInputDevicesDelegate>(RegisterRawInputDevices, registerAddress);
        _getRawInputDataHook = context.Hooks.CreateHook<GetRawInputDataDelegate>(GetRawInputData, rawDataAddress);
        _getCursorPosHook = context.Hooks.CreateHook<GetCursorPosDelegate>(GetCursorPos, getCursorAddress);
        _setCursorPosHook = context.Hooks.CreateHook<SetCursorPosDelegate>(SetCursorPos, setCursorAddress);
        _peekMessageHook.Activate();
        _registerHook.Activate();
        _getRawInputDataHook.Activate();
        _getCursorPosHook.Activate();
        _setCursorPosHook.Activate();

        nint xinput = Native.GetModuleHandleW("XINPUT1_3.dll");
        if (xinput != 0)
        {
            nint getStateAddress = Native.GetProcAddress(xinput, "XInputGetState");
            if (getStateAddress != 0)
            {
                _xInputGetStateHook = context.Hooks.CreateHook<XInputGetStateDelegate>(XInputGetState, getStateAddress);
                _xInputGetStateHook.Activate();
                _logger.WriteLine($"[P3R CamFix] XInputGetState trace hook active at 0x{getStateAddress:X}.");
            }
        }
        if (_xInputGetStateHook == null)
            _logger.WriteLine("[P3R CamFix] XInput1_3!XInputGetState was unavailable; gamepad trace disabled.", System.Drawing.Color.Orange);
        _flushTimer = new Timer(_ => FlushReady(), null, 500, 500);
        _logger.WriteLine($"[P3R CamFix] Raw input trace active: {tracePath} (capacity {capacity}).");
    }

    private int PeekMessageW(nint messagePointer, nint window, uint filterMin, uint filterMax, uint removeFlags)
    {
        long enter = Stopwatch.GetTimestamp();
        int result = _peekMessageHook.OriginalFunction(messagePointer, window, filterMin, filterMax, removeFlags);
        long exit = Stopwatch.GetTimestamp();
        if (result == 0 || messagePointer == 0 || (removeFlags & PmRemove) == 0)
            return result;

        uint message = *(uint*)(messagePointer + 0x08);
        uint messageTime = *(uint*)(messagePointer + 0x20);
        if (message == WmMouseMove)
        {
            nint lParam = *(nint*)(messagePointer + 0x18);
            Reserve(new TraceSlot
            {
                Kind = TraceKind.WindowMouse,
                QpcEnter = enter,
                QpcExit = exit,
                OsThread = Native.GetCurrentThreadId(),
                RemoveFlags = removeFlags,
                Result = result,
                X = unchecked((short)((long)lParam & 0xFFFF)),
                Y = unchecked((short)(((long)lParam >> 16) & 0xFFFF)),
                Message = message,
                MessageTime = messageTime,
            });
            return result;
        }
        if (message != WmInput)
            return result;

        nint rawHandle = *(nint*)(messagePointer + 0x18);
        uint bufferSize = 256;
        byte* buffer = stackalloc byte[(int)bufferSize];
        uint read = Native.GetRawInputData(rawHandle, RidInput, buffer, &bufferSize, RawInputHeaderSizeX64);
        if (read == uint.MaxValue || read < RawInputHeaderSizeX64)
            return result;

        uint rawType = *(uint*)(buffer + 0x00);
        uint rawSize = *(uint*)(buffer + 0x04);
        ushort mouseFlags = 0;
        int x = 0;
        int y = 0;
        if (rawType == RimTypeMouse && read >= 44)
        {
            mouseFlags = *(ushort*)(buffer + 0x18);
            x = *(int*)(buffer + 0x24);
            y = *(int*)(buffer + 0x28);
        }

        Reserve(new TraceSlot
        {
            Kind = TraceKind.RawMouse,
            QpcEnter = enter,
            QpcExit = exit,
            OsThread = Native.GetCurrentThreadId(),
            RemoveFlags = removeFlags,
            Result = result,
            RawType = rawType,
            RawSize = rawSize,
            MouseFlags = mouseFlags,
            X = x,
            Y = y,
            Message = message,
            MessageTime = messageTime,
        });
        return result;
    }

    private uint GetRawInputData(nint rawInput, uint command, nint data, uint* size, uint headerSize)
    {
        long enter = Stopwatch.GetTimestamp();
        uint result = _getRawInputDataHook.OriginalFunction(rawInput, command, data, size, headerSize);
        long exit = Stopwatch.GetTimestamp();
        if (command != RidInput || data == 0 || result == uint.MaxValue || result < RawInputHeaderSizeX64)
            return result;

        byte* buffer = (byte*)data;
        uint rawType = *(uint*)(buffer + 0x00);
        uint rawSize = *(uint*)(buffer + 0x04);
        ushort mouseFlags = 0;
        int x = 0, y = 0;
        if (rawType == RimTypeMouse && result >= 44)
        {
            mouseFlags = *(ushort*)(buffer + 0x18);
            x = *(int*)(buffer + 0x24);
            y = *(int*)(buffer + 0x28);
        }
        Reserve(new TraceSlot
        {
            Kind = TraceKind.RawApi,
            QpcEnter = enter,
            QpcExit = exit,
            OsThread = Native.GetCurrentThreadId(),
            Result = unchecked((int)result),
            RawType = rawType,
            RawSize = rawSize,
            MouseFlags = mouseFlags,
            X = x,
            Y = y,
        });
        return result;
    }

    private int GetCursorPos(nint point)
    {
        long enter = Stopwatch.GetTimestamp();
        int result = _getCursorPosHook.OriginalFunction(point);
        long exit = Stopwatch.GetTimestamp();
        Reserve(new TraceSlot
        {
            Kind = TraceKind.GetCursor,
            QpcEnter = enter,
            QpcExit = exit,
            OsThread = Native.GetCurrentThreadId(),
            Result = result,
            X = result != 0 && point != 0 ? *(int*)point : 0,
            Y = result != 0 && point != 0 ? *(int*)(point + 4) : 0,
        });
        return result;
    }

    private int SetCursorPos(int x, int y)
    {
        long enter = Stopwatch.GetTimestamp();
        int result = _setCursorPosHook.OriginalFunction(x, y);
        long exit = Stopwatch.GetTimestamp();
        Reserve(new TraceSlot
        {
            Kind = TraceKind.SetCursor,
            QpcEnter = enter,
            QpcExit = exit,
            OsThread = Native.GetCurrentThreadId(),
            Result = result,
            X = x,
            Y = y,
        });
        return result;
    }

    private uint XInputGetState(uint userIndex, nint statePointer)
    {
        long enter = Stopwatch.GetTimestamp();
        uint result = _xInputGetStateHook!.OriginalFunction(userIndex, statePointer);
        long exit = Stopwatch.GetTimestamp();

        var slot = new TraceSlot
        {
            Kind = TraceKind.XInputState,
            QpcEnter = enter,
            QpcExit = exit,
            OsThread = Native.GetCurrentThreadId(),
            Result = unchecked((int)result),
            UserIndex = userIndex,
        };
        if (result == 0 && statePointer != 0)
        {
            byte* state = (byte*)statePointer;
            slot.Packet = *(uint*)(state + 0x00);
            slot.Buttons = *(ushort*)(state + 0x04);
            slot.LeftTrigger = *(byte*)(state + 0x06);
            slot.RightTrigger = *(byte*)(state + 0x07);
            slot.LeftX = *(short*)(state + 0x08);
            slot.LeftY = *(short*)(state + 0x0A);
            slot.RightX = *(short*)(state + 0x0C);
            slot.RightY = *(short*)(state + 0x0E);
        }
        Reserve(slot);
        return result;
    }

    private int RegisterRawInputDevices(nint devices, uint deviceCount, uint deviceSize)
    {
        long enter = Stopwatch.GetTimestamp();
        int result = _registerHook.OriginalFunction(devices, deviceCount, deviceSize);
        long exit = Stopwatch.GetTimestamp();

        if (devices != 0 && deviceSize >= 16)
        {
            for (uint index = 0; index < deviceCount; index++)
            {
                byte* device = (byte*)devices + index * deviceSize;
                Reserve(new TraceSlot
                {
                    Kind = TraceKind.RegisterDevice,
                    QpcEnter = enter,
                    QpcExit = exit,
                    OsThread = Native.GetCurrentThreadId(),
                    Result = result,
                    UsagePage = *(ushort*)(device + 0x00),
                    Usage = *(ushort*)(device + 0x02),
                    DeviceFlags = *(uint*)(device + 0x04),
                    Target = *(nint*)(device + 0x08),
                });
            }
        }
        return result;
    }

    private void Reserve(TraceSlot value)
    {
        int index = Interlocked.Increment(ref _reserved) - 1;
        if ((uint)index >= (uint)_slots.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        value.Sequence = index;
        _slots[index] = value;
        Volatile.Write(ref _slots[index].Ready, 1);
    }

    private void FlushReady()
    {
        if (_disposed) return;
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
            if (Volatile.Read(ref slot.Ready) == 0) break;
            WriteSlot(slot);
            _read++;
        }
        _writer.Flush();
    }

    private void WriteSlot(in TraceSlot slot)
    {
        _writer.Write(slot.Sequence.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Kind switch
        {
            TraceKind.RawMouse => "raw",
            TraceKind.RawApi => "raw_api",
            TraceKind.WindowMouse => "mouse_move",
            TraceKind.GetCursor => "get_cursor",
            TraceKind.SetCursor => "set_cursor",
            TraceKind.RegisterDevice => "register",
            _ => "xinput"
        });
        _writer.Write(','); _writer.Write(slot.QpcEnter.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.QpcExit.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.OsThread.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RemoveFlags.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Result.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RawType.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RawSize.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.MouseFlags.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.X.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Y.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.UsagePage.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Usage.ToString(CultureInfo.InvariantCulture));
        _writer.Write(",0x"); _writer.Write(slot.DeviceFlags.ToString("X", CultureInfo.InvariantCulture));
        _writer.Write(",0x"); _writer.Write(slot.Target.ToString("X", CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.UserIndex.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Packet.ToString(CultureInfo.InvariantCulture));
        _writer.Write(",0x"); _writer.Write(slot.Buttons.ToString("X", CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.LeftTrigger.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RightTrigger.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.LeftX.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.LeftY.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RightX.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.RightY.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.Message.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.MessageTime.ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_writerLock)
        {
            if (_disposed) return;
            DrainReadyLocked();
            _disposed = true;
            _writer.WriteLine($"# captured={_read.ToString(CultureInfo.InvariantCulture)}");
            _writer.WriteLine($"# dropped={Volatile.Read(ref _dropped).ToString(CultureInfo.InvariantCulture)}");
            _writer.Dispose();
        }
        _flushTimer.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PeekMessageWDelegate(nint messagePointer, nint window, uint filterMin, uint filterMax, uint removeFlags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int RegisterRawInputDevicesDelegate(nint devices, uint deviceCount, uint deviceSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetRawInputDataDelegate(nint rawInput, uint command, nint data, uint* size, uint headerSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetCursorPosDelegate(nint point);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetCursorPosDelegate(int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint userIndex, nint statePointer);

    private enum TraceKind { RawMouse, RawApi, WindowMouse, GetCursor, SetCursor, RegisterDevice, XInputState }

    private struct TraceSlot
    {
        public int Ready;
        public int Sequence;
        public TraceKind Kind;
        public long QpcEnter;
        public long QpcExit;
        public uint OsThread;
        public uint RemoveFlags;
        public int Result;
        public uint RawType;
        public uint RawSize;
        public ushort MouseFlags;
        public int X;
        public int Y;
        public ushort UsagePage;
        public ushort Usage;
        public uint DeviceFlags;
        public nint Target;
        public uint UserIndex;
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
        public uint Message;
        public uint MessageTime;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern nint GetProcAddress(nint module, string procName);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(nint rawInput, uint command, void* data, uint* size, uint headerSize);
    }
}

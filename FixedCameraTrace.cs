using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using System.Diagnostics;
using System.Globalization;

namespace p3rpc.camfix;

/// <summary>
/// Diagnostic trace for the literal-fixed and spline/rail camera output paths.
/// The earlier candidate at 0x1111690 is an initializer and intentionally is
/// not hooked here.
/// </summary>
internal sealed unsafe class FixedCameraTrace : IDisposable
{
    // UFldOperationCamera::TickComponent override (vtable slot 106). This is
    // the owning per-frame dispatcher: it resolves CurrentHitRef->camera (or
    // the operator's free camera), switches on FldOperator state, and updates
    // the selected camera with DeltaTime.
    private const string OperationTickSignature =
        "40 53 48 83 EC 40 0F 29 74 24 30 48 8B D9 0F 28 F1 E8 ?? ?? ?? ?? 48 83 BB B0 00 00 00 00 0F 84 ?? ?? ?? ??";

    // CDO-derived vtable RVAs for this exact executable build. They guard all
    // reads of subclass-specific camera-hit fields.
    private const int FldCameraHitBoxVtableRva = 0x42939A8;
    private const int FldCameraHitSplineVtableRva = 0x4294058;

    // This routine reads a fixed camera through context+0x2E8, copies a view
    // transform, then scales context+0x2CC/+0x2D0 by MarginYaw/MarginPitch.
    private const string PipelineSignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B 01 49 8B F8 48 8B DA 48 8B F1 FF 90 60 06 00 00 48 8B D3 48 8B C8 4C 8B 08 41 FF 91 98 06 00 00 0F 10 07 48 8B 86 E8 02 00 00";

    // AFldCameraHitSpline counterpart. It selects map key 0 through virtual
    // slot 204, copies the selected camera transform, and scales the transient
    // axes at +0x2CC/+0x2D0 by MarginYaw/Pitch at +0x368/+0x36C.
    private const string SplinePipelineSignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B 01 49 8B F8 48 8B DA 48 8B F1 FF 90 60 06 00 00 48 8B D3 48 8B C8 4C 8B 08 41 FF 91 98 06 00 00 0F 10 07 48 8B 5C 24 40 0F 11 86 A8 02 00 00";

    // AFldCameraFixed output helper. It reads camera+0x270->FixedYaw and emits
    // an FRotator when the fixed path is active.
    private const string RotationSignature =
        "48 89 5C 24 08 57 48 83 EC 30 48 8B 01 48 8B DA 48 8B F9 FF 90 88 06 00 00";

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks _hooks;
    private readonly nint _imageBase;
    private readonly TraceSlot[] _slots;
    private readonly StreamWriter _writer;
    private readonly Timer _flushTimer;
    private readonly object _writerLock = new();
    private IHook<FixedPipelineDelegate>? _pipelineHook;
    private IHook<FixedPipelineDelegate>? _splinePipelineHook;
    private IHook<FixedRotationDelegate>? _rotationHook;
    private IHook<OperationTickDelegate>? _operationTickHook;
    private int _reserved;
    private int _read;
    private int _dropped;
    private bool _disposed;

    public FixedCameraTrace(ModContext context, nint imageBase)
    {
        _logger = context.Logger;
        _hooks = context.Hooks!;
        _imageBase = imageBase;
        int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
        _slots = new TraceSlot[capacity];

        string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
        string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, $"camera-modes-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
        _writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        _writer.WriteLine($"# process_start_utc={Process.GetCurrentProcess().StartTime.ToUniversalTime():O}");
        _writer.WriteLine($"# image_base=0x{imageBase:X}");
        _writer.WriteLine("sequence,kind,qpc_enter,qpc_exit,os_thread,context,camera,behavior,operation_owner,current_hit,operator_state,delta_time,input_x_before,input_y_before,input_x_after,input_y_after,transient_yaw_before,transient_pitch_before,transient_yaw_after,transient_pitch_after,fixed_yaw,fixed_pitch,margin_yaw,margin_pitch,rot_speed,scene_yaw_before,scene_pitch_before,scene_yaw_after,scene_pitch_after,output_pitch,output_yaw,output_roll");
        _writer.Flush();
        _flushTimer = new Timer(_ => FlushReady(), null, 500, 500);

        context.StartupScanner.AddMainModuleScan(OperationTickSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Camera-operation tick signature not found; per-frame mode trace disabled.", System.Drawing.Color.Red);
                return;
            }
            nint address = _imageBase + result.Offset;
            _operationTickHook = _hooks.CreateHook<OperationTickDelegate>(OperationTick, address);
            _operationTickHook.Activate();
            _logger.WriteLine($"[P3R CamFix] Camera-operation per-frame trace active at P3R.exe+0x{result.Offset:X}: {tracePath}");
        });

        context.StartupScanner.AddMainModuleScan(PipelineSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Fixed pipeline signature not found; trace disabled.", System.Drawing.Color.Red);
                return;
            }
            nint address = _imageBase + result.Offset;
            _pipelineHook = _hooks.CreateHook<FixedPipelineDelegate>(FixedPipeline, address);
            _pipelineHook.Activate();
            _logger.WriteLine($"[P3R CamFix] Fixed pipeline trace active at P3R.exe+0x{result.Offset:X}: {tracePath}");
        });

        context.StartupScanner.AddMainModuleScan(SplinePipelineSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Spline pipeline signature not found; trace disabled.", System.Drawing.Color.Red);
                return;
            }
            nint address = _imageBase + result.Offset;
            _splinePipelineHook = _hooks.CreateHook<FixedPipelineDelegate>(SplinePipeline, address);
            _splinePipelineHook.Activate();
            _logger.WriteLine($"[P3R CamFix] Spline pipeline trace active at P3R.exe+0x{result.Offset:X}: {tracePath}");
        });

        context.StartupScanner.AddMainModuleScan(RotationSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Fixed rotation signature not found; output trace disabled.", System.Drawing.Color.Red);
                return;
            }
            nint address = _imageBase + result.Offset;
            _rotationHook = _hooks.CreateHook<FixedRotationDelegate>(FixedRotation, address);
            _rotationHook.Activate();
            _logger.WriteLine($"[P3R CamFix] Fixed rotation trace active at P3R.exe+0x{result.Offset:X}: {tracePath}");
        });
    }

    private void FixedPipeline(nint context, nint argument, nint transform)
    {
        long enter = Stopwatch.GetTimestamp();
        nint camera = ReadPointer(context, 0x2E8);
        var slot = CreateSlot(TraceKind.Pipeline, enter, context, camera);
        slot.TransientYawBefore = ReadFloat(context, 0x2CC);
        slot.TransientPitchBefore = ReadFloat(context, 0x2D0);
        ReadCamera(camera, ref slot, before: true);

        _pipelineHook!.OriginalFunction(context, argument, transform);

        slot.QpcExit = Stopwatch.GetTimestamp();
        slot.TransientYawAfter = ReadFloat(context, 0x2CC);
        slot.TransientPitchAfter = ReadFloat(context, 0x2D0);
        ReadCamera(camera, ref slot, before: false);
        if (transform != 0)
        {
            slot.OutputPitch = ReadFloat(transform, 0x00);
            slot.OutputYaw = ReadFloat(transform, 0x04);
            slot.OutputRoll = ReadFloat(transform, 0x08);
        }
        Reserve(slot);
    }

    private nint FixedRotation(nint camera, nint output)
    {
        long enter = Stopwatch.GetTimestamp();
        var slot = CreateSlot(TraceKind.Rotation, enter, 0, camera);
        ReadCamera(camera, ref slot, before: true);

        nint result = _rotationHook!.OriginalFunction(camera, output);

        slot.QpcExit = Stopwatch.GetTimestamp();
        ReadCamera(camera, ref slot, before: false);
        if (output != 0)
        {
            slot.OutputPitch = ReadFloat(output, 0x00);
            slot.OutputYaw = ReadFloat(output, 0x04);
            slot.OutputRoll = ReadFloat(output, 0x08);
        }
        Reserve(slot);
        return result;
    }

    private void SplinePipeline(nint context, nint argument, nint transform)
    {
        long enter = Stopwatch.GetTimestamp();
        nint camera = ReadSplineCamera(context, 0);
        var slot = CreateSlot(TraceKind.SplinePipeline, enter, context, camera);
        slot.TransientYawBefore = ReadFloat(context, 0x2CC);
        slot.TransientPitchBefore = ReadFloat(context, 0x2D0);
        slot.MarginYaw = ReadFloat(context, 0x368);
        slot.MarginPitch = ReadFloat(context, 0x36C);
        slot.RotSpeed = ReadFloat(context, 0x370);
        ReadCamera(camera, ref slot, before: true, readFixedBehavior: false);

        _splinePipelineHook!.OriginalFunction(context, argument, transform);

        slot.QpcExit = Stopwatch.GetTimestamp();
        slot.TransientYawAfter = ReadFloat(context, 0x2CC);
        slot.TransientPitchAfter = ReadFloat(context, 0x2D0);
        ReadCamera(camera, ref slot, before: false, readFixedBehavior: false);
        if (transform != 0)
        {
            slot.OutputPitch = ReadFloat(transform, 0x00);
            slot.OutputYaw = ReadFloat(transform, 0x04);
            slot.OutputRoll = ReadFloat(transform, 0x08);
        }
        Reserve(slot);
    }

    private void OperationTick(nint operation, float deltaTime)
    {
        long enter = Stopwatch.GetTimestamp();
        nint owner = ReadPointer(operation, 0xB0);
        nint hit = ReadPointer(operation, 0xB8);
        nint camera = ResolveOperationCamera(owner, hit);
        var slot = CreateSlot(TraceKind.OperationTick, enter, operation, camera);
        slot.OperationOwner = owner;
        slot.CurrentHit = hit;
        slot.DeltaTime = deltaTime;
        slot.OperatorState = owner == 0 ? -1 : *(int*)(owner + 0x284);
        ReadHitState(hit, ref slot, before: true);
        ReadCamera(camera, ref slot, before: true, readFixedBehavior: false);

        _operationTickHook!.OriginalFunction(operation, deltaTime);

        slot.QpcExit = Stopwatch.GetTimestamp();
        ReadHitState(hit, ref slot, before: false);
        ReadCamera(camera, ref slot, before: false, readFixedBehavior: false);
        Reserve(slot);
    }

    private nint ResolveOperationCamera(nint owner, nint hit)
    {
        if (hit == 0)
            return ReadPointer(owner, 0x240);

        nint vtable = ReadPointer(hit, 0);
        if (vtable == _imageBase + FldCameraHitSplineVtableRva)
            return ReadSplineCamera(hit, 0);
        if (vtable == _imageBase + FldCameraHitBoxVtableRva)
            return ReadPointer(hit, 0x2E8);
        return 0;
    }

    private void ReadHitState(nint hit, ref TraceSlot slot, bool before)
    {
        if (hit == 0) return;
        nint vtable = ReadPointer(hit, 0);
        float yaw;
        float pitch;
        if (vtable == _imageBase + FldCameraHitSplineVtableRva)
        {
            yaw = ReadFloat(hit, 0x2CC);
            pitch = ReadFloat(hit, 0x2D0);
            slot.MarginYaw = ReadFloat(hit, 0x368);
            slot.MarginPitch = ReadFloat(hit, 0x36C);
            slot.RotSpeed = ReadFloat(hit, 0x370);
        }
        else if (vtable == _imageBase + FldCameraHitBoxVtableRva)
        {
            yaw = ReadFloat(hit, 0x2CC);
            pitch = ReadFloat(hit, 0x2D0);
        }
        else
        {
            return;
        }

        if (before)
        {
            slot.TransientYawBefore = yaw;
            slot.TransientPitchBefore = pitch;
        }
        else
        {
            slot.TransientYawAfter = yaw;
            slot.TransientPitchAfter = pitch;
        }
    }

    private static nint ReadSplineCamera(nint context, int key)
    {
        if (context == 0) return 0;
        nint data = ReadPointer(context, 0x2F8);
        int max = *(int*)(context + 0x304);
        if (data == 0 || max is <= 0 or > 1024) return 0;
        for (int slot = 0; slot < max; slot++)
        {
            nint element = data + slot * 24;
            if (*(int*)element == key)
                return *(nint*)(element + 8);
        }
        return 0;
    }

    private static TraceSlot CreateSlot(TraceKind kind, long enter, nint context, nint camera) => new()
    {
        Kind = kind,
        QpcEnter = enter,
        OsThread = Native.GetCurrentThreadId(),
        Context = context,
        Camera = camera,
        Behavior = 0,
        OperationOwner = 0,
        CurrentHit = 0,
        OperatorState = -1,
        DeltaTime = float.NaN,
        InputXBefore = float.NaN,
        InputYBefore = float.NaN,
        InputXAfter = float.NaN,
        InputYAfter = float.NaN,
        TransientYawBefore = float.NaN,
        TransientPitchBefore = float.NaN,
        TransientYawAfter = float.NaN,
        TransientPitchAfter = float.NaN,
        FixedYaw = float.NaN,
        FixedPitch = float.NaN,
        MarginYaw = float.NaN,
        MarginPitch = float.NaN,
        RotSpeed = float.NaN,
        SceneYawBefore = float.NaN,
        ScenePitchBefore = float.NaN,
        SceneYawAfter = float.NaN,
        ScenePitchAfter = float.NaN,
        OutputPitch = float.NaN,
        OutputYaw = float.NaN,
        OutputRoll = float.NaN,
    };

    private static void ReadCamera(nint camera, ref TraceSlot slot, bool before, bool readFixedBehavior = true)
    {
        if (camera == 0) return;
        float inputX = ReadFloat(camera, 0x25C);
        float inputY = ReadFloat(camera, 0x260);
        nint yawScene = ReadPointer(camera, 0x220);
        nint pitchScene = ReadPointer(camera, 0x228);
        float sceneYaw = yawScene == 0 ? float.NaN : ReadFloat(yawScene, 0x12C);
        float scenePitch = pitchScene == 0 ? float.NaN : ReadFloat(pitchScene, 0x128);
        if (before)
        {
            slot.InputXBefore = inputX;
            slot.InputYBefore = inputY;
            slot.SceneYawBefore = sceneYaw;
            slot.ScenePitchBefore = scenePitch;
        }
        else
        {
            slot.InputXAfter = inputX;
            slot.InputYAfter = inputY;
            slot.SceneYawAfter = sceneYaw;
            slot.ScenePitchAfter = scenePitch;
        }

        if (!readFixedBehavior) return;
        nint behavior = ReadPointer(camera, 0x270);
        slot.Behavior = behavior;
        if (behavior == 0) return;
        slot.FixedYaw = ReadFloat(behavior, 0xC8);
        slot.FixedPitch = ReadFloat(behavior, 0xCC);
        slot.MarginYaw = ReadFloat(behavior, 0xD0);
        slot.MarginPitch = ReadFloat(behavior, 0xD4);
        slot.RotSpeed = ReadFloat(behavior, 0xD8);
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
            if (!_disposed) DrainReadyLocked();
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
        _writer.Write(',');
        _writer.Write(slot.Kind switch
        {
            TraceKind.Pipeline => "fixed_pipeline",
            TraceKind.SplinePipeline => "spline_pipeline",
            TraceKind.OperationTick => "operation_tick",
            _ => "fixed_rotation",
        });
        _writer.Write(','); _writer.Write(slot.QpcEnter.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.QpcExit.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.OsThread.ToString(CultureInfo.InvariantCulture));
        WritePointer(slot.Context); WritePointer(slot.Camera); WritePointer(slot.Behavior);
        WritePointer(slot.OperationOwner); WritePointer(slot.CurrentHit);
        _writer.Write(','); _writer.Write(slot.OperatorState.ToString(CultureInfo.InvariantCulture));
        WriteFloat(slot.DeltaTime);
        WriteFloat(slot.InputXBefore); WriteFloat(slot.InputYBefore); WriteFloat(slot.InputXAfter); WriteFloat(slot.InputYAfter);
        WriteFloat(slot.TransientYawBefore); WriteFloat(slot.TransientPitchBefore); WriteFloat(slot.TransientYawAfter); WriteFloat(slot.TransientPitchAfter);
        WriteFloat(slot.FixedYaw); WriteFloat(slot.FixedPitch); WriteFloat(slot.MarginYaw); WriteFloat(slot.MarginPitch); WriteFloat(slot.RotSpeed);
        WriteFloat(slot.SceneYawBefore); WriteFloat(slot.ScenePitchBefore); WriteFloat(slot.SceneYawAfter); WriteFloat(slot.ScenePitchAfter);
        WriteFloat(slot.OutputPitch); WriteFloat(slot.OutputYaw); WriteFloat(slot.OutputRoll);
        _writer.WriteLine();
    }

    private void WritePointer(nint value)
    {
        _writer.Write(",0x");
        _writer.Write(value.ToString("X", CultureInfo.InvariantCulture));
    }

    private void WriteFloat(float value)
    {
        _writer.Write(',');
        _writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static float ReadFloat(nint address, int offset) => address == 0 ? float.NaN : *(float*)(address + offset);
    private static nint ReadPointer(nint address, int offset) => address == 0 ? 0 : *(nint*)(address + offset);

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

    private delegate void FixedPipelineDelegate(nint context, nint argument, nint transform);
    private delegate nint FixedRotationDelegate(nint camera, nint output);
    private delegate void OperationTickDelegate(nint operation, float deltaTime);
    private enum TraceKind { Pipeline, Rotation, SplinePipeline, OperationTick }

    private struct TraceSlot
    {
        public int Ready, Sequence;
        public TraceKind Kind;
        public long QpcEnter, QpcExit;
        public uint OsThread;
        public nint Context, Camera, Behavior, OperationOwner, CurrentHit;
        public int OperatorState;
        public float DeltaTime;
        public float InputXBefore, InputYBefore, InputXAfter, InputYAfter;
        public float TransientYawBefore, TransientPitchBefore, TransientYawAfter, TransientPitchAfter;
        public float FixedYaw, FixedPitch, MarginYaw, MarginPitch, RotSpeed;
        public float SceneYawBefore, ScenePitchBefore, SceneYawAfter, ScenePitchAfter;
        public float OutputPitch, OutputYaw, OutputRoll;
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}

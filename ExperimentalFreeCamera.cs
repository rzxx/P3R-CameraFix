using p3rpc.camfix.Configuration;
using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Responsive normal/free-camera input replacement. Mouse displacement is
/// inserted at the exact native pitch/yaw delta sites after P3R's velocity
/// filter, while controller demand replaces AFldCameraBase.Input before the
/// otherwise-native free-camera update.
/// </summary>
internal sealed unsafe class ExperimentalFreeCamera : IDisposable
{
    private const string FreeCameraUpdateSignature =
        "48 8B C4 48 89 58 10 48 89 68 18 48 89 70 20 57 48 81 EC F0 00 00 00 0F 29 70 E8 48 8B D9 44 0F 29 50 B8 44 0F 28 D1 44 0F 29 60 98";

    private const string PitchDeltaSignature =
        "F3 0F 58 FE E8 ?? ?? ?? ?? F3 0F 10 83 98 01 00 00 48 8B C8 0F 2F F8";

    private const string YawDeltaSignature =
        "48 8B C8 48 8B 10 0F 28 CE FF 92 68 06 00 00 48 89 AB 34 01 00 00 E9 ?? ?? ?? ??";

    private const int MouseOverrideEnabledOffset = 0x00;
    private const int MousePitchDeltaOffset = 0x04;
    private const int MouseYawDeltaOffset = 0x08;

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks _hooks;
    private readonly ExperimentalSplineCamera? _input;
    private readonly nint _imageBase;
    private readonly nint _mouseOverrideState;

    private IHook<FreeCameraUpdateDelegate>? _freeCameraUpdateHook;
    private IAsmHook? _pitchDeltaHook;
    private IAsmHook? _yawDeltaHook;
    private int _freeUpdateReady;
    private int _pitchReady;
    private int _yawReady;
    private int _allHooksReady;
    private int _traceSequence;

    private readonly TraceSlot[]? _traceSlots;
    private readonly StreamWriter? _traceWriter;
    private readonly Timer? _traceFlushTimer;
    private readonly object _traceWriterLock = new();
    private int _traceReserved;
    private int _traceRead;
    private int _traceDropped;
    private bool _traceDisposed;

    public ExperimentalFreeCamera(ModContext context, nint imageBase, ExperimentalSplineCamera? input)
    {
        _logger = context.Logger;
        _hooks = context.Hooks!;
        _input = input;
        _imageBase = imageBase;
        _mouseOverrideState = Marshal.AllocHGlobal(16);
        new Span<byte>((void*)_mouseOverrideState, 16).Clear();

        if (Mod.Configuration.EnableFreeCameraTrace)
        {
            int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
            _traceSlots = new TraceSlot[capacity];
            string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
            string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
            Directory.CreateDirectory(traceDirectory);
            string tracePath = Path.Combine(traceDirectory, $"free-vnext-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _traceWriter = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
            _traceWriter.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
            _traceWriter.WriteLine($"# image_base=0x{imageBase:X}");
            _traceWriter.WriteLine("sequence,qpc_enter,qpc_exit,operation_sequence,operator_key_state,camera_lock,behavior,owner,owner_behavior_match,owner_flag_258,device,device_switch,mouse_source,delta_time,mouse_x,mouse_y,raw_first_age_ms,raw_last_age_ms,stick_x,stick_y,native_x,native_y,applied_x,applied_y,mouse_override,controller_override,mouse_pitch_delta,mouse_yaw_delta,pitch_before,pitch_after,yaw_before,yaw_after,pitch_speed_before,pitch_speed_after,yaw_speed_before,yaw_speed_after");
            _traceWriter.Flush();
            _traceFlushTimer = new Timer(_ => FlushTrace(), null, 500, 500);
            _logger.WriteLine($"[P3R CamFix] Free-camera debug trace active: {tracePath}");
        }

        context.StartupScanner.AddMainModuleScan(FreeCameraUpdateSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Free-camera update signature not found; normal-camera settings unavailable.", System.Drawing.Color.Red);
                return;
            }

            _logger.WriteLine($"[P3R CamFix] Free-camera update signature resolved at P3R.exe+0x{result.Offset:X}; creating hook.");
            try
            {
                _freeCameraUpdateHook = _hooks.CreateHook<FreeCameraUpdateDelegate>(FreeCameraUpdate, _imageBase + result.Offset);
                _freeCameraUpdateHook.Activate();
                Volatile.Write(ref _freeUpdateReady, 1);
                UpdateReadyState($"update P3R.exe+0x{result.Offset:X}");
            }
            catch (Exception exception)
            {
                _logger.WriteLine($"[P3R CamFix] Free-camera update hook failed: {exception.Message}", System.Drawing.Color.Red);
            }
        });

        if (Mod.Configuration.EnableFreeCameraFix)
        {
            context.StartupScanner.AddMainModuleScan(PitchDeltaSignature, result =>
            {
                if (!result.Found)
                {
                    _logger.WriteLine("[P3R CamFix] Free-camera pitch-delta signature not found; raw mouse disabled.", System.Drawing.Color.Red);
                    return;
                }

                _logger.WriteLine($"[P3R CamFix] Free-camera pitch-delta signature resolved at P3R.exe+0x{result.Offset:X}; creating hook.");
                try
                {
                    _pitchDeltaHook = _hooks.CreateAsmHook(BuildPitchOverrideAssembly(), _imageBase + result.Offset, AsmHookBehaviour.ExecuteFirst);
                    _pitchDeltaHook.Activate();
                    Volatile.Write(ref _pitchReady, 1);
                    UpdateReadyState($"pitch delta P3R.exe+0x{result.Offset:X}");
                }
                catch (Exception exception)
                {
                    _logger.WriteLine($"[P3R CamFix] Free-camera pitch hook failed: {exception.Message}", System.Drawing.Color.Red);
                }
            });

            context.StartupScanner.AddMainModuleScan(YawDeltaSignature, result =>
            {
                if (!result.Found)
                {
                    _logger.WriteLine("[P3R CamFix] Free-camera yaw-delta signature not found; raw mouse disabled.", System.Drawing.Color.Red);
                    return;
                }

                _logger.WriteLine($"[P3R CamFix] Free-camera yaw-delta signature resolved at P3R.exe+0x{result.Offset:X}; creating hook.");
                try
                {
                    _yawDeltaHook = _hooks.CreateAsmHook(BuildYawOverrideAssembly(), _imageBase + result.Offset, AsmHookBehaviour.ExecuteFirst);
                    _yawDeltaHook.Activate();
                    Volatile.Write(ref _yawReady, 1);
                    UpdateReadyState($"yaw delta P3R.exe+0x{result.Offset:X}");
                }
                catch (Exception exception)
                {
                    _logger.WriteLine($"[P3R CamFix] Free-camera yaw hook failed: {exception.Message}", System.Drawing.Color.Red);
                }
            });
        }
    }

    private void FreeCameraUpdate(nint behavior, float deltaTime)
    {
        bool traceEnabled = _traceSlots != null;
        long enter = traceEnabled ? Stopwatch.GetTimestamp() : 0;
        if (behavior != 0 && Mod.Configuration.Enabled)
            EnsureNativeCameraParameters(behavior, Mod.Configuration);

        FreeCameraInputSnapshot input = _input?.GetFreeCameraInputSnapshot() ?? default;
        nint owner = behavior == 0 ? 0 : *(nint*)(behavior + 0xC8);
        bool ownerValid = owner != 0;
        bool ownerBehaviorMatch = traceEnabled && ownerValid && *(nint*)(owner + 0x270) == behavior;
        byte ownerFlag258 = traceEnabled && ownerValid ? *(byte*)(owner + 0x258) : (byte)0;
        float nativeX = ownerValid ? ReadFinite(owner + 0x25C) : 0f;
        float nativeY = ownerValid ? ReadFinite(owner + 0x260) : 0f;
        float nativeZ = ownerValid ? ReadFinite(owner + 0x264) : 0f;
        float appliedX = nativeX;
        float appliedY = nativeY;
        bool mouseOverride = false;
        bool controllerOverride = false;
        float mousePitchDelta = 0f;
        float mouseYawDelta = 0f;

        nint yawComponent = traceEnabled && ownerValid ? *(nint*)(owner + 0x220) : 0;
        nint pitchComponent = traceEnabled && ownerValid ? *(nint*)(owner + 0x228) : 0;
        float pitchBefore = traceEnabled ? ReadComponentPitch(pitchComponent) : float.NaN;
        float yawBefore = traceEnabled ? ReadComponentYaw(yawComponent) : float.NaN;
        float pitchSpeedBefore = traceEnabled && behavior != 0 ? ReadFinite(behavior + 0x118) : float.NaN;
        float yawSpeedBefore = traceEnabled && behavior != 0 ? ReadFinite(behavior + 0xFC) : float.NaN;

        bool nativeOwnership = HasNativeOwnership(ownerValid, input.OperationQpc,
            input.OperatorKeyState, deltaTime);
        bool hooksReady = Volatile.Read(ref _allHooksReady) != 0;

        if (Mod.Configuration.Enabled && Mod.Configuration.EnableFreeCameraFix && hooksReady && nativeOwnership)
        {
            if (input.Device == 1 && Mod.Configuration.EnableRawMouse)
            {
                float yawSensitivity = 0.04f * Math.Clamp(Mod.Configuration.MouseHorizontalSensitivityPercent, 10, 300) / 100f;
                float pitchSensitivity = 0.03f * Math.Clamp(Mod.Configuration.MouseVerticalSensitivityPercent, 10, 300) / 100f;
                float yawDirection = Mod.Configuration.InvertMouseX ? -1f : 1f;
                float pitchDirection = Mod.Configuration.InvertMouseY ? 1f : -1f;
                mouseYawDelta = input.MouseX * yawSensitivity * yawDirection;
                mousePitchDelta = input.MouseY * pitchSensitivity * pitchDirection;

                // A minimal sentinel makes P3R take its normal player-input
                // branch even for a single raw count. The exact angle delta is
                // substituted later at the native pitch/yaw result sites.
                appliedX = mouseYawDelta == 0f ? 0f : MathF.CopySign(0.02f, mouseYawDelta);
                appliedY = mousePitchDelta == 0f ? 0f : MathF.CopySign(0.02f, mousePitchDelta);
                *(float*)(owner + 0x25C) = appliedX;
                *(float*)(owner + 0x260) = appliedY;
                *(float*)(owner + 0x264) = 0f;
                ClearNativeAxisState(behavior);
                *(float*)(_mouseOverrideState + MousePitchDeltaOffset) = mousePitchDelta;
                *(float*)(_mouseOverrideState + MouseYawDeltaOffset) = mouseYawDelta;
                Thread.MemoryBarrier();
                *(int*)(_mouseOverrideState + MouseOverrideEnabledOffset) = 1;
                mouseOverride = true;
            }
            else if (input.Device == 2 &&
                     input.DirectControllerAvailable &&
                     Mod.Configuration.EnableDirectController)
            {
                (appliedX, appliedY) = ApplyControllerCurve(input.StickX, input.StickY);
                *(float*)(owner + 0x25C) = appliedX;
                *(float*)(owner + 0x260) = appliedY;
                *(float*)(owner + 0x264) = 0f;
                ClearNativeAxisState(behavior);
                controllerOverride = true;
            }
        }

        try
        {
            _freeCameraUpdateHook!.OriginalFunction(behavior, deltaTime);
        }
        finally
        {
            if (mouseOverride)
            {
                *(int*)(_mouseOverrideState + MouseOverrideEnabledOffset) = 0;
                Thread.MemoryBarrier();
            }

            if ((mouseOverride || controllerOverride) && ownerValid)
            {
                *(float*)(owner + 0x25C) = nativeX;
                *(float*)(owner + 0x260) = nativeY;
                *(float*)(owner + 0x264) = nativeZ;
            }
        }

        if (traceEnabled)
        {
            long exit = Stopwatch.GetTimestamp();
            ReserveTrace(new TraceSlot
            {
                QpcEnter = enter,
                QpcExit = exit,
                OperationSequence = input.OperationSequence,
                OperatorKeyState = input.OperatorKeyState,
                CameraLock = input.CameraLock,
                Behavior = behavior,
                Owner = owner,
                OwnerBehaviorMatch = ownerBehaviorMatch ? 1 : 0,
                OwnerFlag258 = ownerFlag258,
                Device = input.Device,
                DeviceSwitch = input.DeviceChanged ? 1 : 0,
                MouseSource = input.MouseSource,
                DeltaTime = deltaTime,
                MouseX = input.MouseX,
                MouseY = input.MouseY,
                RawFirstAgeMs = QpcAgeMilliseconds(enter, input.RawFirstQpc),
                RawLastAgeMs = QpcAgeMilliseconds(enter, input.RawLastQpc),
                StickX = input.StickX,
                StickY = input.StickY,
                NativeX = nativeX,
                NativeY = nativeY,
                AppliedX = appliedX,
                AppliedY = appliedY,
                MouseOverride = mouseOverride ? 1 : 0,
                ControllerOverride = controllerOverride ? 1 : 0,
                MousePitchDelta = mousePitchDelta,
                MouseYawDelta = mouseYawDelta,
                PitchBefore = pitchBefore,
                PitchAfter = ReadComponentPitch(pitchComponent),
                YawBefore = yawBefore,
                YawAfter = ReadComponentYaw(yawComponent),
                PitchSpeedBefore = pitchSpeedBefore,
                PitchSpeedAfter = behavior == 0 ? float.NaN : ReadFinite(behavior + 0x118),
                YawSpeedBefore = yawSpeedBefore,
                YawSpeedAfter = behavior == 0 ? float.NaN : ReadFinite(behavior + 0xFC),
            });
        }
    }

    private static (float X, float Y) ApplyControllerCurve(int rawX, int rawY)
    {
        // Direction is part of the replacement input itself. Apply it before
        // radial shaping so deadzone size and curve magnitude stay unchanged.
        float x = NormalizeStick(rawX) * (Mod.Configuration.InvertGamepadX ? -1f : 1f);
        float y = NormalizeStick(rawY) * (Mod.Configuration.InvertGamepadY ? -1f : 1f);
        float magnitude = MathF.Sqrt((x * x) + (y * y));
        float deadzone = Math.Clamp(Mod.Configuration.GamepadDeadzonePercent, 0, 50) / 100f;
        if (magnitude <= deadzone || magnitude <= float.Epsilon)
            return (0f, 0f);

        float normalizedMagnitude = Math.Clamp((magnitude - deadzone) / (1f - deadzone), 0f, 1f);
        float curvedMagnitude = Mod.Configuration.ApplyCameraResponseCurve(normalizedMagnitude);
        float scale = curvedMagnitude / magnitude;
        return (Math.Clamp(x * scale, -1f, 1f), Math.Clamp(y * scale, -1f, 1f));
    }

    private static bool HasNativeOwnership(bool ownerValid, long operationQpc, int operatorKeyState, float deltaTime) =>
        ownerValid && operationQpc != 0 && operatorKeyState == 3 && deltaTime > 0.000001f;

    private static void ClearNativeAxisState(nint behavior)
    {
        if (behavior == 0) return;
        *(float*)(behavior + 0xFC) = 0f;
        *(float*)(behavior + 0x100) = 0f;
        *(float*)(behavior + 0x118) = 0f;
        *(float*)(behavior + 0x11C) = 0f;
    }

    private static void EnsureNativeCameraParameters(nint behavior, Config configuration)
    {
        if (behavior == 0) return;
        EnsureAxisParams(behavior + 0xE8, configuration.GamepadHorizontalSpeed,
            configuration.YawAcceleration, configuration.YawDeceleration,
            configuration.YawPress, configuration.YawRelease);
        EnsureAxisParams(behavior + 0x104, configuration.GamepadVerticalSpeed,
            configuration.PitchAcceleration, configuration.PitchDeceleration,
            configuration.PitchPress, configuration.PitchRelease);
        EnsureAxisParams(behavior + 0x120, configuration.CorrectionSpeed,
            configuration.CorrectionAcceleration, configuration.CorrectionDeceleration,
            configuration.CorrectionPress, configuration.CorrectionRelease);
    }

    private static void EnsureAxisParams(nint axis, float speed, float acceleration, float deceleration, float press, float release)
    {
        speed = SanitizeAxisValue(speed, 2000f);
        acceleration = SanitizeAxisValue(acceleration, 10f);
        deceleration = SanitizeAxisValue(deceleration, 10f);
        press = SanitizeAxisValue(press, 10f);
        release = SanitizeAxisValue(release, 10f);

        const float epsilon = 0.0001f;
        if (MathF.Abs(*(float*)(axis + 0x00) - speed) <= epsilon &&
            MathF.Abs(*(float*)(axis + 0x04) - acceleration) <= epsilon &&
            MathF.Abs(*(float*)(axis + 0x08) - deceleration) <= epsilon &&
            MathF.Abs(*(float*)(axis + 0x0C) - press) <= epsilon &&
            MathF.Abs(*(float*)(axis + 0x10) - release) <= epsilon)
        {
            return;
        }

        *(float*)(axis + 0x00) = speed;
        *(float*)(axis + 0x04) = acceleration;
        *(float*)(axis + 0x08) = deceleration;
        *(float*)(axis + 0x0C) = press;
        *(float*)(axis + 0x10) = release;
        *(float*)(axis + 0x14) = 0f;
        *(float*)(axis + 0x18) = 0f;
    }

    private static float SanitizeAxisValue(float value, float maximum) =>
        Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, maximum);

    private string[] BuildPitchOverrideAssembly() =>
    new[]
    {
        "use64",
        "push rax",
        $"mov rax, 0x{_mouseOverrideState:X}",
        "cmp dword [rax], 0",
        "je free_pitch_override_done",
        "movss xmm6, dword [rax+4]",
        "mov dword [rbx+118h], 0",
        "mov dword [rbx+11Ch], 0",
        "free_pitch_override_done:",
        "pop rax",
    };

    private string[] BuildYawOverrideAssembly() =>
    new[]
    {
        "use64",
        "push rax",
        $"mov rax, 0x{_mouseOverrideState:X}",
        "cmp dword [rax], 0",
        "je free_yaw_override_done",
        "movss xmm6, dword [rax+8]",
        "mov dword [rbx+0FCh], 0",
        "mov dword [rbx+100h], 0",
        "free_yaw_override_done:",
        "pop rax",
    };

    private void UpdateReadyState(string hook)
    {
        _logger.WriteLine($"[P3R CamFix] Free-camera {hook} hook active.");
        if (Volatile.Read(ref _freeUpdateReady) != 0 &&
            Volatile.Read(ref _pitchReady) != 0 &&
            Volatile.Read(ref _yawReady) != 0 &&
            Interlocked.Exchange(ref _allHooksReady, 1) == 0)
        {
            _logger.WriteLine("[P3R CamFix] Free-camera replacement ready.");
        }
    }

    private void ReserveTrace(TraceSlot value)
    {
        if (_traceSlots == null) return;
        int index = Interlocked.Increment(ref _traceReserved) - 1;
        if ((uint)index >= (uint)_traceSlots.Length)
        {
            Interlocked.Increment(ref _traceDropped);
            return;
        }
        value.Sequence = Interlocked.Increment(ref _traceSequence) - 1;
        _traceSlots[index] = value;
        Volatile.Write(ref _traceSlots[index].Ready, 1);
    }

    private void FlushTrace()
    {
        if (_traceWriter == null || _traceDisposed) return;
        lock (_traceWriterLock)
        {
            if (!_traceDisposed) DrainTraceLocked();
        }
    }

    private void DrainTraceLocked()
    {
        if (_traceSlots == null || _traceWriter == null) return;
        int limit = Math.Min(Volatile.Read(ref _traceReserved), _traceSlots.Length);
        while (_traceRead < limit)
        {
            ref TraceSlot slot = ref _traceSlots[_traceRead];
            if (Volatile.Read(ref slot.Ready) == 0) break;
            WriteTrace(slot);
            _traceRead++;
        }
        _traceWriter.Flush();
    }

    private void WriteTrace(in TraceSlot slot)
    {
        TextWriter writer = _traceWriter!;
        writer.Write(slot.Sequence.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.QpcEnter.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.QpcExit.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperationSequence.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperatorKeyState.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.CameraLock.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.Behavior.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.Owner.ToString("X", CultureInfo.InvariantCulture));
        WriteInt(writer, slot.OwnerBehaviorMatch, slot.OwnerFlag258, slot.Device, slot.DeviceSwitch, slot.MouseSource);
        WriteFloat(writer, slot.DeltaTime);
        WriteInt(writer, slot.MouseX, slot.MouseY);
        WriteFloat(writer, slot.RawFirstAgeMs);
        WriteFloat(writer, slot.RawLastAgeMs);
        WriteInt(writer, slot.StickX, slot.StickY);
        WriteFloat(writer, slot.NativeX); WriteFloat(writer, slot.NativeY);
        WriteFloat(writer, slot.AppliedX); WriteFloat(writer, slot.AppliedY);
        WriteInt(writer, slot.MouseOverride, slot.ControllerOverride);
        WriteFloat(writer, slot.MousePitchDelta); WriteFloat(writer, slot.MouseYawDelta);
        WriteFloat(writer, slot.PitchBefore); WriteFloat(writer, slot.PitchAfter);
        WriteFloat(writer, slot.YawBefore); WriteFloat(writer, slot.YawAfter);
        WriteFloat(writer, slot.PitchSpeedBefore); WriteFloat(writer, slot.PitchSpeedAfter);
        WriteFloat(writer, slot.YawSpeedBefore); WriteFloat(writer, slot.YawSpeedAfter);
        writer.WriteLine();
    }

    private static void WriteInt(TextWriter writer, params int[] values)
    {
        foreach (int value in values)
        {
            writer.Write(',');
            writer.Write(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteFloat(TextWriter writer, float value)
    {
        writer.Write(',');
        writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static float NormalizeStick(int value) =>
        value >= 0 ? Math.Min(value, 32767) / 32767f : Math.Max(value, -32768) / 32768f;

    private static float ReadFinite(nint address)
    {
        float value = *(float*)address;
        return float.IsFinite(value) ? value : 0f;
    }

    private static float ReadComponentPitch(nint component) =>
        component == 0 ? float.NaN : *(float*)(component + 0x128);

    private static float ReadComponentYaw(nint component) =>
        component == 0 ? float.NaN : *(float*)(component + 0x12C);

    private static float QpcAgeMilliseconds(long now, long timestamp)
    {
        if (timestamp == 0 || now < timestamp) return float.NaN;
        return (float)((now - timestamp) * 1000.0 / Stopwatch.Frequency);
    }

    public void Dispose()
    {
        if (_traceDisposed) return;
        _freeCameraUpdateHook?.Disable();
        _pitchDeltaHook?.Disable();
        _yawDeltaHook?.Disable();
        *(int*)(_mouseOverrideState + MouseOverrideEnabledOffset) = 0;

        _traceFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_traceWriter != null)
        {
            lock (_traceWriterLock)
            {
                if (!_traceDisposed)
                {
                    DrainTraceLocked();
                    _traceWriter.WriteLine($"# captured={_traceRead.ToString(CultureInfo.InvariantCulture)}");
                    _traceWriter.WriteLine($"# dropped={Volatile.Read(ref _traceDropped).ToString(CultureInfo.InvariantCulture)}");
                    _traceWriter.Dispose();
                }
            }
        }
        _traceDisposed = true;
        _traceFlushTimer?.Dispose();
        Marshal.FreeHGlobal(_mouseOverrideState);
    }

    private delegate void FreeCameraUpdateDelegate(nint behavior, float deltaTime);

    private struct TraceSlot
    {
        public int Ready, Sequence, OperationSequence, OperatorKeyState, CameraLock;
        public int OwnerBehaviorMatch, OwnerFlag258, Device, DeviceSwitch, MouseSource, MouseX, MouseY, StickX, StickY;
        public int MouseOverride, ControllerOverride;
        public long QpcEnter, QpcExit;
        public nint Behavior, Owner;
        public float DeltaTime, RawFirstAgeMs, RawLastAgeMs;
        public float NativeX, NativeY, AppliedX, AppliedY;
        public float MousePitchDelta, MouseYawDelta;
        public float PitchBefore, PitchAfter, YawBefore, YawAfter;
        public float PitchSpeedBefore, PitchSpeedAfter, YawSpeedBefore, YawSpeedAfter;
    }
}

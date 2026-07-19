using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Experimental replacement for both spline-camera filters: the 166.7 ms
/// input-target state machine and the subsequent constant 20-degrees/second
/// vector follower. All subclass writes are guarded by the exact live
/// AFldCameraHitSpline vtable for the supported executable.
/// </summary>
internal sealed unsafe class ExperimentalSplineCamera : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint WmSetCursor = 0x0020;
    private const uint PmRemove = 0x0001;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RawInputHeaderSizeX64 = 24;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort MouseUsage = 0x0002;
    private const int FldCameraHitSplineVtableRva = 0x4294058;
    private const int SplineStateOffset = 0x2A8;
    private const float RailMotionHoldDuration = 0.25f;
    private const float RailPositionSpeedThreshold = 5.0f;
    private const float RailAngularSpeedThreshold = 0.5f;
    private const float LegacyFallbackRecentRawGraceSeconds = 0.25f;

    private const string OperationTickSignature =
        "40 53 48 83 EC 40 0F 29 74 24 30 48 8B D9 0F 28 F1 E8 ?? ?? ?? ?? 48 83 BB B0 00 00 00 00 0F 84 ?? ?? ?? ??";

    private const string SplineInterpolatorSignature =
        "48 83 EC 58 F3 0F 10 49 34 0F 57 C0 0F 29 74 24 40 45 33 C0 0F 2F C8 0F 29 7C 24 30 44 0F 29 44 24 20";

    // AFldCameraHitSpline's per-frame update. Its fourth argument is the
    // 28-byte view transform that receives the rail result followed by the
    // bounded yaw/pitch offsets. Capturing it closes the gap between the
    // relative input trace and the player-visible rail orientation.
    private const string SplineUpdateSignature =
        "48 8B C4 48 89 70 10 48 89 78 18 4C 89 70 20 55 48 8D 68 A1 48 81 EC 90 00 00 00 0F 29 70 E8";

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks _hooks;
    private readonly nint _imageBase;
    private readonly IHook<PeekMessageWDelegate>? _peekMessageHook;
    private readonly IHook<SetCursorPosDelegate>? _setCursorPosHook;
    private readonly IHook<SetCursorDelegate>? _setCursorHook;
    private readonly IHook<ShowCursorDelegate>? _showCursorHook;
    private readonly IHook<XInputGetStateDelegate>? _xInputGetStateHook;
    private IHook<OperationTickDelegate>? _operationTickHook;
    private IHook<SplineInterpolatorDelegate>? _splineInterpolatorHook;
    private IHook<SplineUpdateDelegate>? _splineUpdateHook;

    private int _pendingMouseX;
    private int _pendingMouseY;
    private long _pendingMouseFirstQpc;
    private long _pendingMouseLastQpc;
    private int _pendingWarpRejected;
    private int _pendingWarpX;
    private int _pendingWarpY;
    private int _pendingWmSetCursor;
    private int _pendingSetCursorCalls;
    private int _pendingSetCursorZeroCalls;
    private int _pendingSetCursorNonzeroCalls;
    private int _pendingSetCursorSuppressedCalls;
    private int _pendingShowCursorCalls;
    private nint _lastSetCursorRequested;
    private long _lastSetCursorQpc;
    private int _lastShowCursorShow;
    private int _lastShowCursorResult;
    private long _lastShowCursorQpc;
    private long _lastSplineUpdateQpc;
    private float _lastSplineDeltaTime;
    private long _lastNativeOwnershipQpc;
    private long _lastNativeInputDisabledQpc;
    private long _lastAcceptedRawMouseQpc;
    private int _lastOperatorKeyState;
    private int _splineInputEnabledSinceResume;
    private int _frameMouseX;
    private int _frameMouseY;
    private long _frameMouseFirstQpc;
    private long _frameMouseLastQpc;
    private long _frameOperationQpc;
    private int _frameOperatorKeyState;
    private int _frameOperatorState;
    private int _frameOperatorNextState;
    private int _frameCameraLock;
    private nint _frameKernelInput;
    private nint _frameDefaultInputComponent;
    private nint _frameCurrentInputComponent;
    private int _frameShowMouseCursor;
    private nint _framePlayerInput;
    private int _frameWarpRejected;
    private int _frameWarpX;
    private int _frameWarpY;
    private int _frameWmSetCursor;
    private int _frameSetCursorCalls;
    private int _frameSetCursorZeroCalls;
    private int _frameSetCursorNonzeroCalls;
    private int _frameSetCursorSuppressedCalls;
    private int _frameShowCursorCalls;
    private int _rightStickX;
    private int _rightStickY;
    private int _lastOperationStickX;
    private int _lastOperationStickY;
    private int _activeDevice;
    private int _operationSequence;
    private int _lastRawInputOperation;
    private int _lastRegistrationCheckOperation;
    private bool _mouseBecameActive;
    private bool _deviceChangedThisFrame;
    private float _mouseIdleSeconds;
    private RecenterReason _recenterReason;
    private nint _recenterHit;
    private float _recenterStartYawDegrees;
    private float _recenterStartPitchDegrees;
    private float _recenterElapsed;
    private float _recenterProgress;
    private bool _recenterCancelledThisFrame;
    private nint _lastRailHit;
    private bool _haveRailTransform;
    private float _lastRailX;
    private float _lastRailY;
    private float _lastRailZ;
    private float _lastRailYaw;
    private float _lastRailPitch;
    private float _railMotionHoldSeconds;
    private float _lastRailPositionDelta;
    private float _lastRailAngleDelta;
    private nint _messageWindow;
    private int _warpCandidateX;
    private int _warpCandidateY;
    private long _warpCandidateQpc;
    private int _warpCandidateArmed;
    private int _cursorVisible;
    private int _cursorX;
    private int _cursorY;
    private nint _cursorHandle;
    private MouseSource _frameMouseSource;
    private bool _operationHookReady;
    private bool _interpolatorHookReady;
    private bool _splineUpdateHookReady;
    private bool _pendingTraceReady;
    private TraceSlot _pendingTrace;

    private readonly TraceSlot[]? _traceSlots;
    private readonly StreamWriter? _traceWriter;
    private readonly Timer? _traceFlushTimer;
    private readonly object _traceWriterLock = new();
    private int _traceReserved;
    private int _traceRead;
    private int _traceDropped;
    private bool _traceDisposed;

    public ExperimentalSplineCamera(ModContext context, nint imageBase)
    {
        _logger = context.Logger;
        _hooks = context.Hooks!;
        _imageBase = imageBase;

        if (Mod.Configuration.EnableExperimentalSplineTrace)
        {
            int capacity = Math.Clamp(Mod.Configuration.CameraFilterTraceCapacity, 1024, 2_000_000);
            _traceSlots = new TraceSlot[capacity];
            string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
            string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
            Directory.CreateDirectory(traceDirectory);
            string tracePath = Path.Combine(traceDirectory, $"spline-vnext-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _traceWriter = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
            _traceWriter.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
            _traceWriter.WriteLine($"# image_base=0x{imageBase:X}");
            _traceWriter.WriteLine("sequence,qpc,operation_sequence,operator_key_state,operator_state,operator_next_state,camera_lock,kernel_input,default_input_component,current_input_component,show_mouse_cursor,player_input,hit,device,device_switch,mouse_source,delta_time,mouse_x,mouse_y,raw_first_age_ms,raw_last_age_ms,operation_to_camera_ms,camera_input_frozen,native_disable_age_ms,recenter_reason,recenter_progress,recenter_cancelled,mouse_idle_seconds,rail_motion_active,rail_position_delta,rail_angle_delta,cursor_visible,cursor_x,cursor_y,cursor_handle,wm_setcursor_count,setcursor_count,setcursor_zero_count,setcursor_nonzero_count,setcursor_suppressed_count,last_setcursor_handle,last_setcursor_age_ms,showcursor_count,last_showcursor_show,last_showcursor_result,last_showcursor_age_ms,warp_rejected_count,warp_x,warp_y,warp_candidate_armed,warp_candidate_x,warp_candidate_y,warp_candidate_age_ms,stick_x,stick_y,native_x,native_y,margin_yaw,margin_pitch,desired_x,desired_y,output_before_x,output_before_y,output_after_x,output_after_y,degrees_before_pitch,degrees_before_yaw,degrees_after_pitch,degrees_after_yaw,view_x,view_y,view_z,view_angle_0,view_angle_1,view_angle_2,view_fov,rail_angle_0,rail_angle_1");
            _traceWriter.Flush();
            _traceFlushTimer = new Timer(_ => FlushTrace(), null, 500, 500);
            _logger.WriteLine($"[P3R CamFix] Experimental spline trace active: {tracePath}");
        }

        try
        {
            nint user32 = Native.GetModuleHandleW("user32.dll");
            nint peekMessage = user32 == 0 ? 0 : Native.GetProcAddress(user32, "PeekMessageW");
            if (peekMessage == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32!PeekMessageW");

            _peekMessageHook = _hooks.CreateHook<PeekMessageWDelegate>(PeekMessageW, peekMessage);
            _peekMessageHook.Activate();
            _logger.WriteLine("[P3R CamFix] Experimental spline raw-mouse capture active.");
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Raw mouse capture unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        try
        {
            nint user32 = Native.GetModuleHandleW("user32.dll");
            nint setCursorPos = user32 == 0 ? 0 : Native.GetProcAddress(user32, "SetCursorPos");
            if (setCursorPos == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32!SetCursorPos");

            _setCursorPosHook = _hooks.CreateHook<SetCursorPosDelegate>(SetCursorPos, setCursorPos);
            _setCursorPosHook.Activate();
            _logger.WriteLine("[P3R CamFix] Experimental cursor-warp correlation active.");
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Cursor-warp correlation unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        try
        {
            nint user32 = Native.GetModuleHandleW("user32.dll");
            nint setCursor = user32 == 0 ? 0 : Native.GetProcAddress(user32, "SetCursor");
            nint showCursor = user32 == 0 ? 0 : Native.GetProcAddress(user32, "ShowCursor");
            if (setCursor == 0 || showCursor == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32 cursor-state APIs");

            _setCursorHook = _hooks.CreateHook<SetCursorDelegate>(SetCursor, setCursor);
            _showCursorHook = _hooks.CreateHook<ShowCursorDelegate>(ShowCursor, showCursor);
            _setCursorHook.Activate();
            _showCursorHook.Activate();
            _logger.WriteLine("[P3R CamFix] Experimental cursor-state call tracing active.");
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Cursor-state call tracing unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        try
        {
            nint xinput = Native.GetModuleHandleW("XINPUT1_3.dll");
            nint getState = xinput == 0 ? 0 : Native.GetProcAddress(xinput, "XInputGetState");
            if (getState != 0)
            {
                _xInputGetStateHook = _hooks.CreateHook<XInputGetStateDelegate>(XInputGetState, getState);
                _xInputGetStateHook.Activate();
                _logger.WriteLine("[P3R CamFix] Experimental direct spline-controller capture active.");
            }
            else
            {
                _logger.WriteLine("[P3R CamFix] XInput1_3!XInputGetState unavailable; spline controller will use the game's axis input.", System.Drawing.Color.Orange);
            }
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Direct spline-controller capture unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        context.StartupScanner.AddMainModuleScan(OperationTickSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Experimental spline operation-tick signature not found; raw mouse input disabled.", System.Drawing.Color.Red);
                return;
            }

            _operationTickHook = _hooks.CreateHook<OperationTickDelegate>(OperationTick, _imageBase + result.Offset);
            _operationTickHook.Activate();
            _operationHookReady = true;
            LogReadyState($"operation tick P3R.exe+0x{result.Offset:X}");
        });

        context.StartupScanner.AddMainModuleScan(SplineInterpolatorSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Experimental spline interpolator signature not found; spline fix disabled.", System.Drawing.Color.Red);
                return;
            }

            _splineInterpolatorHook = _hooks.CreateHook<SplineInterpolatorDelegate>(SplineInterpolator, _imageBase + result.Offset);
            _splineInterpolatorHook.Activate();
            _interpolatorHookReady = true;
            LogReadyState($"interpolator P3R.exe+0x{result.Offset:X}");
        });

        context.StartupScanner.AddMainModuleScan(SplineUpdateSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Experimental spline-update signature not found; moving-idle recenter and final rail-transform fields unavailable.", System.Drawing.Color.Orange);
                return;
            }

            _splineUpdateHook = _hooks.CreateHook<SplineUpdateDelegate>(SplineUpdate, _imageBase + result.Offset);
            _splineUpdateHook.Activate();
            _splineUpdateHookReady = true;
            LogReadyState($"view transform P3R.exe+0x{result.Offset:X}");
        });
    }

    private int PeekMessageW(nint messagePointer, nint window, uint filterMin, uint filterMax, uint removeFlags)
    {
        int result = _peekMessageHook!.OriginalFunction(messagePointer, window, filterMin, filterMax, removeFlags);
        if (result == 0 || messagePointer == 0)
            return result;

        nint messageWindow = *(nint*)messagePointer;
        if (messageWindow != 0)
            Volatile.Write(ref _messageWindow, messageWindow);
        if ((removeFlags & PmRemove) == 0)
            return result;

        uint message = *(uint*)(messagePointer + 0x08);
        if (message == WmSetCursor)
            Interlocked.Increment(ref _pendingWmSetCursor);
        if (message != WmInput)
            return result;

        nint rawHandle = *(nint*)(messagePointer + 0x18);
        uint size = 256;
        byte* buffer = stackalloc byte[(int)size];
        uint read = Native.GetRawInputData(rawHandle, RidInput, buffer, &size, RawInputHeaderSizeX64);
        if (read < 44 || read == uint.MaxValue || *(uint*)buffer != RimTypeMouse || (*(ushort*)(buffer + 0x18) & MouseMoveAbsolute) != 0)
            return result;

        int x = *(int*)(buffer + 0x24);
        int y = *(int*)(buffer + 0x28);
        if (IsRecentCursorWarpPacket(x, y))
        {
            Interlocked.Increment(ref _pendingWarpRejected);
            Volatile.Write(ref _pendingWarpX, x);
            Volatile.Write(ref _pendingWarpY, y);
            _logger.WriteLine($"[P3R CamFix] Rejected cursor-warp raw packet ({x}, {y}).");
            return result;
        }
        if (x != 0) Interlocked.Add(ref _pendingMouseX, x);
        if (y != 0) Interlocked.Add(ref _pendingMouseY, y);
        if (x != 0 || y != 0)
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref _pendingMouseFirstQpc, now, 0);
            Volatile.Write(ref _pendingMouseLastQpc, now);
            Volatile.Write(ref _lastAcceptedRawMouseQpc, now);
            Volatile.Write(ref _lastRawInputOperation, Volatile.Read(ref _operationSequence));
        }
        return result;
    }

    private int SetCursorPos(int x, int y)
    {
        Point before = default;
        int haveBefore = Native.GetCursorPos(&before);
        int result = _setCursorPosHook!.OriginalFunction(x, y);
        if (result == 0 || haveBefore == 0 || !Mod.Configuration.EnableSplineCursorWarpRejection)
            return result;

        int deltaX = x - before.X;
        int deltaY = y - before.Y;
        int threshold = Math.Clamp(Mod.Configuration.SplineCursorWarpMinimumCounts, 64, 8192);
        if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < threshold)
            return result;

        Volatile.Write(ref _warpCandidateX, deltaX);
        Volatile.Write(ref _warpCandidateY, deltaY);
        Volatile.Write(ref _warpCandidateQpc, Stopwatch.GetTimestamp());
        Volatile.Write(ref _warpCandidateArmed, 1);
        return result;
    }

    private nint SetCursor(nint cursor)
    {
        long now = Stopwatch.GetTimestamp();
        nint appliedCursor = cursor;
        if (cursor != 0 && ShouldSuppressGameplayCursor(now))
        {
            appliedCursor = 0;
            Interlocked.Increment(ref _pendingSetCursorSuppressedCalls);
        }

        nint result = _setCursorHook!.OriginalFunction(appliedCursor);
        Interlocked.Increment(ref _pendingSetCursorCalls);
        if (cursor == 0)
            Interlocked.Increment(ref _pendingSetCursorZeroCalls);
        else
            Interlocked.Increment(ref _pendingSetCursorNonzeroCalls);
        Volatile.Write(ref _lastSetCursorRequested, cursor);
        Volatile.Write(ref _lastSetCursorQpc, now);
        return result;
    }

    private int ShowCursor(int show)
    {
        int result = _showCursorHook!.OriginalFunction(show);
        Interlocked.Increment(ref _pendingShowCursorCalls);
        Volatile.Write(ref _lastShowCursorShow, show != 0 ? 1 : 0);
        Volatile.Write(ref _lastShowCursorResult, result);
        Volatile.Write(ref _lastShowCursorQpc, Stopwatch.GetTimestamp());
        return result;
    }

    private bool IsRecentCursorWarpPacket(int x, int y)
    {
        if (!Mod.Configuration.EnableSplineCursorWarpRejection || Volatile.Read(ref _warpCandidateArmed) == 0)
            return false;

        long age = Stopwatch.GetTimestamp() - Volatile.Read(ref _warpCandidateQpc);
        if (age < 0 || age > Stopwatch.Frequency / 10)
        {
            Volatile.Write(ref _warpCandidateArmed, 0);
            return false;
        }

        int candidateX = Volatile.Read(ref _warpCandidateX);
        int candidateY = Volatile.Read(ref _warpCandidateY);
        int tolerance = Math.Clamp(Mod.Configuration.SplineCursorWarpMatchTolerance, 0, 64);
        if (!MatchesCursorWarpPacket(x, y, candidateX, candidateY, tolerance))
            return false;

        Volatile.Write(ref _warpCandidateArmed, 0);
        return true;
    }

    private static bool MatchesCursorWarpPacket(int packetX, int packetY, int warpX, int warpY, int tolerance) =>
        Math.Abs((long)packetX - warpX) <= tolerance && Math.Abs((long)packetY - warpY) <= tolerance;

    private uint XInputGetState(uint userIndex, nint statePointer)
    {
        uint result = _xInputGetStateHook!.OriginalFunction(userIndex, statePointer);
        if (result == 0 && statePointer != 0 && userIndex == 0)
        {
            Volatile.Write(ref _rightStickX, *(short*)(statePointer + 0x0C));
            Volatile.Write(ref _rightStickY, *(short*)(statePointer + 0x0E));
        }
        return result;
    }

    private void OperationTick(nint operation, float deltaTime)
    {
        _operationSequence++;
        _frameOperationQpc = Stopwatch.GetTimestamp();
        CaptureNativeInputOwnership(operation);
        _frameMouseX = Interlocked.Exchange(ref _pendingMouseX, 0);
        _frameMouseY = Interlocked.Exchange(ref _pendingMouseY, 0);
        _frameMouseFirstQpc = Interlocked.Exchange(ref _pendingMouseFirstQpc, 0);
        _frameMouseLastQpc = Interlocked.Exchange(ref _pendingMouseLastQpc, 0);
        _frameWarpRejected = Interlocked.Exchange(ref _pendingWarpRejected, 0);
        _frameWarpX = Interlocked.Exchange(ref _pendingWarpX, 0);
        _frameWarpY = Interlocked.Exchange(ref _pendingWarpY, 0);
        _frameWmSetCursor = Interlocked.Exchange(ref _pendingWmSetCursor, 0);
        _frameSetCursorCalls = Interlocked.Exchange(ref _pendingSetCursorCalls, 0);
        _frameSetCursorZeroCalls = Interlocked.Exchange(ref _pendingSetCursorZeroCalls, 0);
        _frameSetCursorNonzeroCalls = Interlocked.Exchange(ref _pendingSetCursorNonzeroCalls, 0);
        _frameSetCursorSuppressedCalls = Interlocked.Exchange(ref _pendingSetCursorSuppressedCalls, 0);
        _frameShowCursorCalls = Interlocked.Exchange(ref _pendingShowCursorCalls, 0);
        _frameMouseSource = (_frameMouseX != 0 || _frameMouseY != 0)
            ? MouseSource.Raw
            : _frameWarpRejected != 0 ? MouseSource.WarpRejected : MouseSource.None;
        CaptureCursorState();

        if (AnyExperimentalRawMouseEnabled() &&
            _operationSequence - Volatile.Read(ref _lastRawInputOperation) > 30 &&
            (_lastRegistrationCheckOperation == 0 || _operationSequence - _lastRegistrationCheckOperation >= 300))
        {
            _lastRegistrationCheckOperation = _operationSequence;
            EnsureRawMouseRegistration();
        }
        int stickX = Volatile.Read(ref _rightStickX);
        int stickY = Volatile.Read(ref _rightStickY);
        InputDevice previous = (InputDevice)Volatile.Read(ref _activeDevice);
        InputDevice next = previous;

        if (AnyExperimentalRawMouseEnabled() && (_frameMouseX != 0 || _frameMouseY != 0))
        {
            next = InputDevice.Mouse;
        }
        else if (AnyExperimentalDirectControllerEnabled() && _xInputGetStateHook != null)
        {
            float x = NormalizeStick(stickX);
            float y = NormalizeStick(stickY);
            float deltaX = NormalizeStick(stickX - _lastOperationStickX);
            float deltaY = NormalizeStick(stickY - _lastOperationStickY);
            float configuredDeadzone = Mod.Configuration.EnableExperimentalFreeCamera
                ? Math.Min(Mod.Configuration.SplineControllerDeadzone, Mod.Configuration.FreeControllerDeadzone)
                : Mod.Configuration.SplineControllerDeadzone;
            float switchThreshold = Math.Max(0.02f, Math.Clamp(configuredDeadzone, 0f, 0.95f) * 0.5f);
            bool deliberateChange = (deltaX * deltaX) + (deltaY * deltaY) >= 0.0001f;
            bool outsideSwitchZone = (x * x) + (y * y) >= switchThreshold * switchThreshold;
            if (deliberateChange && outsideSwitchZone)
                next = InputDevice.Controller;
        }

        _mouseBecameActive = next == InputDevice.Mouse && previous != InputDevice.Mouse;
        _deviceChangedThisFrame = next != previous;
        if (next != previous)
        {
            Volatile.Write(ref _activeDevice, (int)next);
            _logger.WriteLine($"[P3R CamFix] Experimental spline device: {previous} -> {next}.");
        }
        _lastOperationStickX = stickX;
        _lastOperationStickY = stickY;

        _operationTickHook!.OriginalFunction(operation, deltaTime);

        _frameMouseX = 0;
        _frameMouseY = 0;
        _frameMouseFirstQpc = 0;
        _frameMouseLastQpc = 0;
        _frameOperationQpc = 0;
        _frameOperatorKeyState = 0;
        _frameOperatorState = 0;
        _frameOperatorNextState = 0;
        _frameCameraLock = 0;
        _frameKernelInput = 0;
        _frameDefaultInputComponent = 0;
        _frameCurrentInputComponent = 0;
        _frameShowMouseCursor = 0;
        _framePlayerInput = 0;
        _frameWarpRejected = 0;
        _frameWarpX = 0;
        _frameWarpY = 0;
        _frameWmSetCursor = 0;
        _frameSetCursorCalls = 0;
        _frameSetCursorZeroCalls = 0;
        _frameSetCursorNonzeroCalls = 0;
        _frameSetCursorSuppressedCalls = 0;
        _frameShowCursorCalls = 0;
        _frameMouseSource = MouseSource.None;
        _mouseBecameActive = false;
        _deviceChangedThisFrame = false;
    }

    private void SplineInterpolator(nint state, nint input, float deltaTime)
    {
        if (!Mod.Configuration.EnableExperimentalSplineCamera || !IsSplineState(state) || input == 0)
        {
            _splineInterpolatorHook!.OriginalFunction(state, input, deltaTime);
            return;
        }

        nint hit = state - SplineStateOffset;
        MarkSplineGameplayActive(deltaTime);
        float marginYaw = SafeMargin(*(float*)(hit + 0x368));
        float marginPitch = SafeMargin(*(float*)(hit + 0x36C));
        float nativeX = ClampNative(*(float*)input);
        float nativeY = ClampNative(*(float*)(input + 4));
        float outputBeforeX = ClampFinite(*(float*)(state + 0x24));
        float outputBeforeY = ClampFinite(*(float*)(state + 0x28));
        float degreesBeforePitch = *(float*)(hit + 0x2A0);
        float degreesBeforeYaw = *(float*)(hit + 0x2A4);
        InputDevice device = (InputDevice)Volatile.Read(ref _activeDevice);

        // WM_INPUT registration can disappear across menus/maps. The stock
        // legacy mouse path remains distinguishable by its exact 0.006 steps.
        // Use it both to recover mouse ownership after controller use and as a
        // deterministic fallback until raw registration is available again.
        if (Mod.Configuration.EnableSplineLegacyMouseFallback &&
            device != InputDevice.Mouse &&
            IsStickCenteredForDeviceSwitch() &&
            IsQuantizedMouseInput(nativeX, nativeY))
        {
            InputDevice previous = device;
            device = InputDevice.Mouse;
            Volatile.Write(ref _activeDevice, (int)device);
            _mouseBecameActive = true;
            _deviceChangedThisFrame = true;
            _logger.WriteLine($"[P3R CamFix] Experimental spline device: {previous} -> Mouse (legacy-axis evidence).");
        }

        float desiredX;
        float desiredY;
        float outputX;
        float outputY;
        bool cameraInputFrozen = ShouldFreezeCameraInput(deltaTime, _frameOperatorKeyState);
        bool nativeInputLocked = _frameOperatorKeyState != 3;
        bool railMotionActive = ConsumeRailMotionState(hit, deltaTime);
        bool rawMouseMovement = device == InputDevice.Mouse &&
                                _frameMouseSource == MouseSource.Raw &&
                                (_frameMouseX != 0 || _frameMouseY != 0);
        bool legacyMouseMovement = device == InputDevice.Mouse &&
                                   _frameMouseSource == MouseSource.None &&
                                   Mod.Configuration.EnableSplineLegacyMouseFallback &&
                                   (Math.Abs(nativeX) > 0.0001f || Math.Abs(nativeY) > 0.0001f);
        if (legacyMouseMovement)
        {
            float rawAgeSeconds = QpcAgeSeconds(Stopwatch.GetTimestamp(), Volatile.Read(ref _lastAcceptedRawMouseQpc));
            if (ShouldDeferLegacyFallback(
                    _recenterReason == RecenterReason.NativeLock,
                    _recenterProgress,
                    rawAgeSeconds))
            {
                legacyMouseMovement = false;
                _frameMouseSource = MouseSource.LegacyDeferred;
            }
            else
            {
                _frameMouseSource = MouseSource.Legacy;
            }
        }
        bool acceptedMouseMovement = rawMouseMovement || legacyMouseMovement;

        (float controllerDemandX, float controllerDemandY) =
            device == InputDevice.Controller && Mod.Configuration.EnableSplineDirectController && _xInputGetStateHook != null
                ? ApplyControllerCurve(Volatile.Read(ref _rightStickX), Volatile.Read(ref _rightStickY))
                : (Math.Clamp(nativeX, -1f, 1f), Math.Clamp(nativeY, -1f, 1f));
        bool acceptedCameraDemand = device == InputDevice.Mouse && Mod.Configuration.EnableSplineRawMouse
            ? acceptedMouseMovement
            : (controllerDemandX * controllerDemandX) + (controllerDemandY * controllerDemandY) > 0.000001f;

        _recenterCancelledThisFrame = false;
        if (!cameraInputFrozen && device == InputDevice.Mouse && Mod.Configuration.EnableSplineRawMouse)
        {
            if (_mouseBecameActive || acceptedMouseMovement)
                _mouseIdleSeconds = 0f;
            else
                _mouseIdleSeconds += Math.Clamp(deltaTime, 0f, 0.1f);
        }
        else if (device != InputDevice.Mouse)
        {
            _mouseIdleSeconds = 0f;
        }

        if (nativeInputLocked && Mod.Configuration.EnableSplineNativeLockRecenter)
            BeginAutonomousRecenter(RecenterReason.NativeLock, hit, outputBeforeX, outputBeforeY, marginYaw, marginPitch);

        if (!cameraInputFrozen && _recenterReason != RecenterReason.None && acceptedCameraDemand)
            CancelAutonomousRecenter();

        float idleDelay = Math.Clamp(Mod.Configuration.SplineMouseIdleRecenterDelay, 0f, 30f);
        if (!cameraInputFrozen &&
            _recenterReason == RecenterReason.None &&
            device == InputDevice.Mouse &&
            Mod.Configuration.EnableSplineRawMouse &&
            Mod.Configuration.EnableSplineMouseIdleRecenter &&
            !acceptedMouseMovement &&
            _mouseIdleSeconds >= idleDelay &&
            railMotionActive &&
            DegreeVectorLength(outputBeforeX, outputBeforeY, marginYaw, marginPitch) > 0.01f)
        {
            BeginAutonomousRecenter(RecenterReason.MovingIdle, hit, outputBeforeX, outputBeforeY, marginYaw, marginPitch);
        }

        bool mayAdvanceRecenter = deltaTime > 0.000001f &&
                                  _recenterReason != RecenterReason.None &&
                                  (!nativeInputLocked || Mod.Configuration.EnableSplineNativeLockRecenter);
        if (mayAdvanceRecenter)
        {
            (desiredX, desiredY, outputX, outputY) = AdvanceAutonomousRecenter(
                hit, deltaTime, outputBeforeX, outputBeforeY, marginYaw, marginPitch);
        }
        else if (cameraInputFrozen)
        {
            // P3R's AFldOperator.KeyState is the native input-ownership gate:
            // 3 (Enable) admits camera input; 0/1/2 do not. Dialogue and rail
            // transitions can retain positive DeltaTime while KeyState is 1.
            // A paused zero-DeltaTime frame may arm a lock recenter, but it
            // cannot advance the visible camera until time resumes.
            desiredX = _recenterReason == RecenterReason.None ? ClampFinite(*(float*)(state + 0x18)) : 0f;
            desiredY = _recenterReason == RecenterReason.None ? ClampFinite(*(float*)(state + 0x1C)) : 0f;
            outputX = outputBeforeX;
            outputY = outputBeforeY;
        }
        else if (device == InputDevice.Mouse && Mod.Configuration.EnableSplineRawMouse)
        {
            desiredX = (_mouseBecameActive || _recenterCancelledThisFrame) ? outputBeforeX : ClampFinite(*(float*)(state + 0x18));
            desiredY = (_mouseBecameActive || _recenterCancelledThisFrame) ? outputBeforeY : ClampFinite(*(float*)(state + 0x1C));
            float yawSensitivity = Math.Clamp(Mod.Configuration.SplineMouseYawDegreesPerCount, 0f, 10f);
            float pitchSensitivity = Math.Clamp(Mod.Configuration.SplineMousePitchDegreesPerCount, 0f, 10f);
            desiredX = Math.Clamp(desiredX + (_frameMouseX * yawSensitivity / marginYaw), -1f, 1f);
            float direction = Mod.Configuration.InvertSplineMouseY ? 1f : -1f;
            desiredY = Math.Clamp(desiredY + (_frameMouseY * pitchSensitivity * direction / marginPitch), -1f, 1f);

            if (_frameMouseSource == MouseSource.Legacy)
            {
                float dt = Math.Clamp(deltaTime, 0f, 0.1f);
                float yawSpeed = Math.Clamp(Mod.Configuration.SplineLegacyMouseYawSpeed, 0f, 2000f);
                float pitchSpeed = Math.Clamp(Mod.Configuration.SplineLegacyMousePitchSpeed, 0f, 2000f);
                desiredX = Math.Clamp(desiredX + (nativeX * yawSpeed * dt / marginYaw), -1f, 1f);
                desiredY = Math.Clamp(desiredY + (nativeY * pitchSpeed * dt / marginPitch), -1f, 1f);
                _frameMouseSource = MouseSource.Legacy;
            }

            float alpha = ExponentialAlpha(deltaTime, Mod.Configuration.SplineMouseSmoothing);
            outputX = Lerp(outputBeforeX, desiredX, alpha);
            outputY = Lerp(outputBeforeY, desiredY, alpha);
        }
        else
        {
            desiredX = controllerDemandX;
            desiredY = controllerDemandY;

            float priorDesiredX = ClampFinite(*(float*)(state + 0x18));
            float priorDesiredY = ClampFinite(*(float*)(state + 0x1C));
            float hysteresis = Math.Clamp(Mod.Configuration.SplineControllerTargetHysteresis, 0f, 0.25f);
            float targetDx = desiredX - priorDesiredX;
            float targetDy = desiredY - priorDesiredY;
            if ((targetDx * targetDx) + (targetDy * targetDy) < hysteresis * hysteresis)
            {
                desiredX = priorDesiredX;
                desiredY = priorDesiredY;
            }
            float alpha = ControllerAlpha(deltaTime, outputBeforeX, outputBeforeY, desiredX, desiredY);
            outputX = Lerp(outputBeforeX, desiredX, alpha);
            outputY = Lerp(outputBeforeY, desiredY, alpha);
        }

        outputX = ClampFinite(outputX);
        outputY = ClampFinite(outputY);
        WriteReplacementState(state, desiredX, desiredY, outputX, outputY);

        // +0x11035F0 would otherwise chase this output at a fixed 20 deg/s.
        // Seed its current vector to the same value so it contributes no second
        // filter; our single FPS-independent response above owns the motion.
        WriteVector(hit + 0x29C, 0f, outputY * marginPitch, outputX * marginYaw);

        long traceQpc = Stopwatch.GetTimestamp();
        var trace = new TraceSlot
        {
            Qpc = traceQpc,
            OperationSequence = _operationSequence,
            OperatorKeyState = _frameOperatorKeyState,
            OperatorState = _frameOperatorState,
            OperatorNextState = _frameOperatorNextState,
            CameraLock = _frameCameraLock,
            KernelInput = _frameKernelInput,
            DefaultInputComponent = _frameDefaultInputComponent,
            CurrentInputComponent = _frameCurrentInputComponent,
            ShowMouseCursor = _frameShowMouseCursor,
            PlayerInput = _framePlayerInput,
            Hit = hit,
            Device = device,
            DeviceSwitch = _deviceChangedThisFrame ? 1 : 0,
            MouseSource = _frameMouseSource,
            DeltaTime = deltaTime,
            MouseX = _frameMouseX,
            MouseY = _frameMouseY,
            RawFirstAgeMs = QpcAgeMilliseconds(traceQpc, _frameMouseFirstQpc),
            RawLastAgeMs = QpcAgeMilliseconds(traceQpc, _frameMouseLastQpc),
            OperationToCameraMs = QpcAgeMilliseconds(traceQpc, _frameOperationQpc),
            CameraInputFrozen = cameraInputFrozen ? 1 : 0,
            NativeDisableAgeMs = QpcAgeMilliseconds(traceQpc, Volatile.Read(ref _lastNativeInputDisabledQpc)),
            RecenterReason = _recenterReason,
            RecenterProgress = _recenterProgress,
            RecenterCancelled = _recenterCancelledThisFrame ? 1 : 0,
            MouseIdleSeconds = _mouseIdleSeconds,
            RailMotionActive = railMotionActive ? 1 : 0,
            RailPositionDelta = _lastRailPositionDelta,
            RailAngleDelta = _lastRailAngleDelta,
            CursorVisible = _cursorVisible,
            CursorX = _cursorX,
            CursorY = _cursorY,
            CursorHandle = _cursorHandle,
            WmSetCursor = _frameWmSetCursor,
            SetCursorCalls = _frameSetCursorCalls,
            SetCursorZeroCalls = _frameSetCursorZeroCalls,
            SetCursorNonzeroCalls = _frameSetCursorNonzeroCalls,
            SetCursorSuppressedCalls = _frameSetCursorSuppressedCalls,
            LastSetCursorHandle = Volatile.Read(ref _lastSetCursorRequested),
            LastSetCursorAgeMs = QpcAgeMilliseconds(traceQpc, Volatile.Read(ref _lastSetCursorQpc)),
            ShowCursorCalls = _frameShowCursorCalls,
            LastShowCursorShow = Volatile.Read(ref _lastShowCursorShow),
            LastShowCursorResult = Volatile.Read(ref _lastShowCursorResult),
            LastShowCursorAgeMs = QpcAgeMilliseconds(traceQpc, Volatile.Read(ref _lastShowCursorQpc)),
            WarpRejected = _frameWarpRejected,
            WarpX = _frameWarpX,
            WarpY = _frameWarpY,
            WarpCandidateArmed = Volatile.Read(ref _warpCandidateArmed),
            WarpCandidateX = Volatile.Read(ref _warpCandidateX),
            WarpCandidateY = Volatile.Read(ref _warpCandidateY),
            WarpCandidateAgeMs = CursorWarpCandidateAgeMilliseconds(),
            StickX = Volatile.Read(ref _rightStickX),
            StickY = Volatile.Read(ref _rightStickY),
            NativeX = nativeX,
            NativeY = nativeY,
            MarginYaw = marginYaw,
            MarginPitch = marginPitch,
            DesiredX = desiredX,
            DesiredY = desiredY,
            OutputBeforeX = outputBeforeX,
            OutputBeforeY = outputBeforeY,
            OutputAfterX = outputX,
            OutputAfterY = outputY,
            DegreesBeforePitch = degreesBeforePitch,
            DegreesBeforeYaw = degreesBeforeYaw,
            DegreesAfterPitch = outputY * marginPitch,
            DegreesAfterYaw = outputX * marginYaw,
            ViewX = float.NaN,
            ViewY = float.NaN,
            ViewZ = float.NaN,
            ViewAngle0 = float.NaN,
            ViewAngle1 = float.NaN,
            ViewAngle2 = float.NaN,
            ViewFov = float.NaN,
            RailAngle0 = float.NaN,
            RailAngle1 = float.NaN,
        };

        if (_splineUpdateHookReady)
        {
            _pendingTrace = trace;
            _pendingTraceReady = true;
        }
        else
        {
            ReserveTrace(trace);
        }
    }

    private byte SplineUpdate(nint hit, float deltaTime, nint sourceTransform, nint outputTransform)
    {
        // A completed call without an interpolator record must not inherit a
        // record from an earlier frame or camera state.
        _pendingTraceReady = false;
        byte result = _splineUpdateHook!.OriginalFunction(hit, deltaTime, sourceTransform, outputTransform);

        if (_pendingTraceReady && _pendingTrace.Hit == hit && _pendingTrace.OperationSequence == _operationSequence)
        {
            TraceSlot trace = _pendingTrace;
            if (outputTransform != 0)
            {
                trace.ViewX = ReadFloat(outputTransform, 0x00);
                trace.ViewY = ReadFloat(outputTransform, 0x04);
                trace.ViewZ = ReadFloat(outputTransform, 0x08);
                trace.ViewAngle0 = ReadFloat(outputTransform, 0x0C);
                trace.ViewAngle1 = ReadFloat(outputTransform, 0x10);
                trace.ViewAngle2 = ReadFloat(outputTransform, 0x14);
                trace.ViewFov = ReadFloat(outputTransform, 0x18);

                // Native +0x11112EA adds yaw margin output to +0x0C and
                // pitch margin output to +0x10. Subtracting our contribution
                // yields the rail/player transform before user offset.
                trace.RailAngle0 = trace.ViewAngle0 - trace.DegreesAfterYaw;
                trace.RailAngle1 = trace.ViewAngle1 - trace.DegreesAfterPitch;
                UpdateRailMotion(
                    hit,
                    deltaTime,
                    trace.ViewX,
                    trace.ViewY,
                    trace.ViewZ,
                    trace.RailAngle0,
                    trace.RailAngle1);
                trace.RailPositionDelta = _lastRailPositionDelta;
                trace.RailAngleDelta = _lastRailAngleDelta;
            }
            ReserveTrace(trace);
        }
        _pendingTraceReady = false;
        return result;
    }

    private void BeginAutonomousRecenter(
        RecenterReason reason,
        nint hit,
        float outputX,
        float outputY,
        float marginYaw,
        float marginPitch)
    {
        if (_recenterReason == reason && _recenterHit == hit)
            return;

        _recenterReason = reason;
        _recenterHit = hit;
        _recenterStartYawDegrees = outputX * marginYaw;
        _recenterStartPitchDegrees = outputY * marginPitch;
        _recenterElapsed = 0f;
        _recenterProgress = 0f;
    }

    private (float DesiredX, float DesiredY, float OutputX, float OutputY) AdvanceAutonomousRecenter(
        nint hit,
        float deltaTime,
        float outputBeforeX,
        float outputBeforeY,
        float marginYaw,
        float marginPitch)
    {
        if (_recenterHit != hit)
            BeginAutonomousRecenter(_recenterReason, hit, outputBeforeX, outputBeforeY, marginYaw, marginPitch);

        float duration = Math.Clamp(Mod.Configuration.SplineAutonomousRecenterDuration, 0.05f, 2f);
        _recenterElapsed = Math.Min(duration, _recenterElapsed + Math.Clamp(deltaTime, 0f, 0.1f));
        float linearProgress = Math.Clamp(_recenterElapsed / duration, 0f, 1f);
        _recenterProgress = SmoothStep(linearProgress);

        float yawDegrees = Lerp(_recenterStartYawDegrees, 0f, _recenterProgress);
        float pitchDegrees = Lerp(_recenterStartPitchDegrees, 0f, _recenterProgress);
        float outputX = linearProgress >= 1f ? 0f : yawDegrees / marginYaw;
        float outputY = linearProgress >= 1f ? 0f : pitchDegrees / marginPitch;
        return (0f, 0f, outputX, outputY);
    }

    private void CancelAutonomousRecenter()
    {
        _recenterReason = RecenterReason.None;
        _recenterHit = 0;
        _recenterElapsed = 0f;
        _recenterProgress = 0f;
        _recenterCancelledThisFrame = true;
    }

    private bool ConsumeRailMotionState(nint hit, float deltaTime)
    {
        if (_haveRailTransform && hit != _lastRailHit)
        {
            _railMotionHoldSeconds = 0f;
            return false;
        }
        bool active = _railMotionHoldSeconds > 0f;
        if (deltaTime > 0f)
            _railMotionHoldSeconds = Math.Max(0f, _railMotionHoldSeconds - Math.Clamp(deltaTime, 0f, 0.1f));
        return active;
    }

    private void UpdateRailMotion(
        nint hit,
        float deltaTime,
        float x,
        float y,
        float z,
        float yaw,
        float pitch)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(yaw) || !float.IsFinite(pitch))
        {
            _haveRailTransform = false;
            _railMotionHoldSeconds = 0f;
            _lastRailPositionDelta = float.NaN;
            _lastRailAngleDelta = float.NaN;
            return;
        }

        if (!_haveRailTransform || hit != _lastRailHit)
        {
            _lastRailHit = hit;
            _haveRailTransform = true;
            _railMotionHoldSeconds = 0f;
            _lastRailPositionDelta = 0f;
            _lastRailAngleDelta = 0f;
        }
        else
        {
            float dx = x - _lastRailX;
            float dy = y - _lastRailY;
            float dz = z - _lastRailZ;
            _lastRailPositionDelta = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            float yawDelta = ShortestAngleDeltaDegrees(_lastRailYaw, yaw);
            float pitchDelta = ShortestAngleDeltaDegrees(_lastRailPitch, pitch);
            _lastRailAngleDelta = MathF.Sqrt((yawDelta * yawDelta) + (pitchDelta * pitchDelta));

            float dt = Math.Clamp(deltaTime, 0f, 0.1f);
            if (IsRailTransformMoving(_lastRailPositionDelta, _lastRailAngleDelta, dt))
            {
                _railMotionHoldSeconds = RailMotionHoldDuration;
            }
        }

        _lastRailX = x;
        _lastRailY = y;
        _lastRailZ = z;
        _lastRailYaw = yaw;
        _lastRailPitch = pitch;
    }

    private static float ControllerAlpha(float deltaTime, float currentX, float currentY, float targetX, float targetY)
    {
        float targetMagnitudeSquared = (targetX * targetX) + (targetY * targetY);
        float currentMagnitudeSquared = (currentX * currentX) + (currentY * currentY);
        if (targetMagnitudeSquared <= 0.000001f && currentMagnitudeSquared > 0.000001f)
            return ExponentialAlpha(deltaTime, Mod.Configuration.SplineControllerRecenterSmoothing);

        float dx = targetX - currentX;
        float dy = targetY - currentY;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
        float threshold = Math.Max(0.001f, Mod.Configuration.SplineControllerLargeChangeThreshold);
        float blend = Math.Clamp(distance / threshold, 0f, 1f);
        blend = blend * blend * (3f - (2f * blend));
        float small = Math.Clamp(Mod.Configuration.SplineControllerSmallSmoothing, 0f, 1f);
        float large = Math.Clamp(Mod.Configuration.SplineControllerLargeSmoothing, 0f, 1f);
        return ExponentialAlpha(deltaTime, Lerp(small, large, blend));
    }

    private static (float X, float Y) ApplyControllerCurve(int rawX, int rawY)
    {
        float x = NormalizeStick(rawX);
        float y = NormalizeStick(rawY);
        float magnitude = MathF.Sqrt((x * x) + (y * y));
        float deadzone = Math.Clamp(Mod.Configuration.SplineControllerDeadzone, 0f, 0.95f);
        if (magnitude <= deadzone || magnitude <= float.Epsilon)
            return (0f, 0f);

        float normalizedMagnitude = Math.Clamp((magnitude - deadzone) / (1f - deadzone), 0f, 1f);
        float exponent = Math.Clamp(Mod.Configuration.SplineControllerResponseExponent, 0.1f, 5f);
        float sensitivity = Math.Clamp(Mod.Configuration.SplineControllerSensitivity, 0f, 4f);
        float curvedMagnitude = MathF.Pow(normalizedMagnitude, exponent) * sensitivity;
        float scale = curvedMagnitude / magnitude;
        return (Math.Clamp(x * scale, -1f, 1f), Math.Clamp(y * scale, -1f, 1f));
    }

    private bool IsSplineState(nint state)
    {
        if (state == 0) return false;
        nint hit = state - SplineStateOffset;
        return *(nint*)hit == _imageBase + FldCameraHitSplineVtableRva;
    }

    private bool IsStickCenteredForDeviceSwitch()
    {
        float x = NormalizeStick(Volatile.Read(ref _rightStickX));
        float y = NormalizeStick(Volatile.Read(ref _rightStickY));
        float threshold = Math.Max(0.02f, Math.Clamp(Mod.Configuration.SplineControllerDeadzone, 0f, 0.95f) * 0.5f);
        return (x * x) + (y * y) < threshold * threshold;
    }

    private static bool IsQuantizedMouseInput(float x, float y)
    {
        const float step = 0.006f;
        const float tolerance = 0.0002f;
        bool xNonzero = Math.Abs(x) > 0.0001f;
        bool yNonzero = Math.Abs(y) > 0.0001f;
        if (!xNonzero && !yNonzero) return false;
        bool xQuantized = !xNonzero || Math.Abs(x - (MathF.Round(x / step) * step)) <= tolerance;
        bool yQuantized = !yNonzero || Math.Abs(y - (MathF.Round(y / step) * step)) <= tolerance;
        return xQuantized && yQuantized;
    }

    private void EnsureRawMouseRegistration()
    {
        nint target = Volatile.Read(ref _messageWindow);
        if (target == 0) return;

        const int capacity = 64;
        RawInputDevice* devices = stackalloc RawInputDevice[capacity];
        uint count = capacity;
        uint registered = Native.GetRegisteredRawInputDevices(devices, &count, (uint)sizeof(RawInputDevice));
        if (registered == uint.MaxValue)
        {
            _logger.WriteLine($"[P3R CamFix] Could not inspect raw-input registration (Win32 {Marshal.GetLastWin32Error()}).", System.Drawing.Color.Orange);
            return;
        }

        for (int index = 0; index < registered; index++)
        {
            if (devices[index].UsagePage == GenericDesktopUsagePage && devices[index].Usage == MouseUsage)
                return;
        }

        RawInputDevice mouse = new()
        {
            UsagePage = GenericDesktopUsagePage,
            Usage = MouseUsage,
            Flags = 0,
            Target = target,
        };
        if (Native.RegisterRawInputDevices(&mouse, 1, (uint)sizeof(RawInputDevice)) != 0)
        {
            _logger.WriteLine($"[P3R CamFix] Restored missing foreground raw-mouse registration for window 0x{target:X}.");
        }
        else
        {
            _logger.WriteLine($"[P3R CamFix] Raw-mouse registration restore failed (Win32 {Marshal.GetLastWin32Error()}); using legacy fallback.", System.Drawing.Color.Orange);
        }
    }

    private static void WriteReplacementState(nint state, float desiredX, float desiredY, float outputX, float outputY)
    {
        WriteVector(state + 0x00, desiredX, desiredY, 0f);
        WriteVector(state + 0x0C, outputX, outputY, 0f);
        WriteVector(state + 0x18, desiredX, desiredY, 0f);
        WriteVector(state + 0x24, outputX, outputY, 0f);
        *(float*)(state + 0x30) = 0f;
        *(float*)(state + 0x34) = 0f;
    }

    private static void WriteVector(nint address, float x, float y, float z)
    {
        *(float*)address = x;
        *(float*)(address + 4) = y;
        *(float*)(address + 8) = z;
    }

    private static float ExponentialAlpha(float deltaTime, float timeConstant)
    {
        if (timeConstant <= 0f) return 1f;
        float dt = Math.Clamp(deltaTime, 0f, 0.1f);
        return dt <= 0f ? 0f : 1f - MathF.Exp(-dt / Math.Clamp(timeConstant, 0.0001f, 1f));
    }

    private static bool ShouldFreezeCameraInput(float deltaTime, int operatorKeyState) =>
        deltaTime <= 0.000001f || operatorKeyState != 3;

    private static float SmoothStep(float value)
    {
        float t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float DegreeVectorLength(float x, float y, float marginYaw, float marginPitch)
    {
        float yaw = x * marginYaw;
        float pitch = y * marginPitch;
        return MathF.Sqrt((yaw * yaw) + (pitch * pitch));
    }

    private static float ShortestAngleDeltaDegrees(float from, float to)
    {
        float delta = (to - from) % 360f;
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        return delta;
    }

    private static bool IsRailTransformMoving(float positionDelta, float angleDelta, float deltaTime) =>
        deltaTime > 0.000001f &&
        ((positionDelta / deltaTime) >= RailPositionSpeedThreshold ||
         (angleDelta / deltaTime) >= RailAngularSpeedThreshold);

    private static bool ShouldDeferLegacyFallback(bool nativeLockRecenterActive, float recenterProgress, float rawAgeSeconds) =>
        nativeLockRecenterActive &&
        recenterProgress < 0.9999f &&
        rawAgeSeconds >= 0f &&
        rawAgeSeconds <= LegacyFallbackRecentRawGraceSeconds;

    private static float QpcAgeSeconds(long now, long timestamp)
    {
        if (timestamp == 0 || now < timestamp)
            return float.PositiveInfinity;
        return (float)((now - timestamp) / (double)Stopwatch.Frequency);
    }

    private static float SafeMargin(float value) => float.IsFinite(value) && Math.Abs(value) > 0.001f ? Math.Abs(value) : 1f;
    private static float ClampFinite(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
    private static float ClampNative(float value) => float.IsFinite(value) ? Math.Clamp(value, -2f, 2f) : 0f;
    private static float NormalizeStick(int value) => value >= 0 ? Math.Min(value, 32767) / 32767f : Math.Max(value, -32768) / 32768f;

    private static bool AnyExperimentalRawMouseEnabled() =>
        (Mod.Configuration.EnableExperimentalSplineCamera && Mod.Configuration.EnableSplineRawMouse) ||
        (Mod.Configuration.EnableExperimentalFreeCamera && Mod.Configuration.EnableFreeRawMouse);

    private static bool AnyExperimentalDirectControllerEnabled() =>
        (Mod.Configuration.EnableExperimentalSplineCamera && Mod.Configuration.EnableSplineDirectController) ||
        (Mod.Configuration.EnableExperimentalFreeCamera && Mod.Configuration.EnableFreeDirectController);

    internal FreeCameraInputSnapshot GetFreeCameraInputSnapshot() => new(
        _operationSequence,
        _frameOperationQpc,
        _frameOperatorKeyState,
        _frameCameraLock,
        (int)(InputDevice)Volatile.Read(ref _activeDevice),
        _deviceChangedThisFrame,
        (int)_frameMouseSource,
        _frameMouseX,
        _frameMouseY,
        _frameMouseFirstQpc,
        _frameMouseLastQpc,
        Volatile.Read(ref _rightStickX),
        Volatile.Read(ref _rightStickY),
        _xInputGetStateHook != null);
    private static float Lerp(float start, float target, float alpha) => start + ((target - start) * alpha);

    private void ReserveTrace(TraceSlot value)
    {
        if (_traceSlots == null) return;
        int index = Interlocked.Increment(ref _traceReserved) - 1;
        if ((uint)index >= (uint)_traceSlots.Length)
        {
            Interlocked.Increment(ref _traceDropped);
            return;
        }
        value.Sequence = index;
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
        var writer = _traceWriter!;
        writer.Write(slot.Sequence.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.Qpc.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperationSequence.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperatorKeyState.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperatorState.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.OperatorNextState.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.CameraLock.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.KernelInput.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.DefaultInputComponent.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.CurrentInputComponent.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.ShowMouseCursor.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.PlayerInput.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.Hit.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.Device.ToString().ToLowerInvariant());
        writer.Write(','); writer.Write(slot.DeviceSwitch.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.MouseSource.ToString().ToLowerInvariant());
        WriteFloat(writer, slot.DeltaTime);
        writer.Write(','); writer.Write(slot.MouseX.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.MouseY.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.RawFirstAgeMs);
        WriteFloat(writer, slot.RawLastAgeMs);
        WriteFloat(writer, slot.OperationToCameraMs);
        writer.Write(','); writer.Write(slot.CameraInputFrozen.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.NativeDisableAgeMs);
        writer.Write(','); writer.Write(slot.RecenterReason.ToString().ToLowerInvariant());
        WriteFloat(writer, slot.RecenterProgress);
        writer.Write(','); writer.Write(slot.RecenterCancelled.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.MouseIdleSeconds);
        writer.Write(','); writer.Write(slot.RailMotionActive.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.RailPositionDelta);
        WriteFloat(writer, slot.RailAngleDelta);
        writer.Write(','); writer.Write(slot.CursorVisible.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.CursorX.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.CursorY.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.CursorHandle.ToString("X", CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WmSetCursor.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.SetCursorCalls.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.SetCursorZeroCalls.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.SetCursorNonzeroCalls.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.SetCursorSuppressedCalls.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x"); writer.Write(slot.LastSetCursorHandle.ToString("X", CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.LastSetCursorAgeMs);
        writer.Write(','); writer.Write(slot.ShowCursorCalls.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.LastShowCursorShow.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.LastShowCursorResult.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.LastShowCursorAgeMs);
        writer.Write(','); writer.Write(slot.WarpRejected.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WarpX.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WarpY.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WarpCandidateArmed.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WarpCandidateX.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.WarpCandidateY.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.WarpCandidateAgeMs);
        writer.Write(','); writer.Write(slot.StickX.ToString(CultureInfo.InvariantCulture));
        writer.Write(','); writer.Write(slot.StickY.ToString(CultureInfo.InvariantCulture));
        WriteFloat(writer, slot.NativeX); WriteFloat(writer, slot.NativeY);
        WriteFloat(writer, slot.MarginYaw); WriteFloat(writer, slot.MarginPitch);
        WriteFloat(writer, slot.DesiredX); WriteFloat(writer, slot.DesiredY);
        WriteFloat(writer, slot.OutputBeforeX); WriteFloat(writer, slot.OutputBeforeY);
        WriteFloat(writer, slot.OutputAfterX); WriteFloat(writer, slot.OutputAfterY);
        WriteFloat(writer, slot.DegreesBeforePitch); WriteFloat(writer, slot.DegreesBeforeYaw);
        WriteFloat(writer, slot.DegreesAfterPitch); WriteFloat(writer, slot.DegreesAfterYaw);
        WriteFloat(writer, slot.ViewX); WriteFloat(writer, slot.ViewY); WriteFloat(writer, slot.ViewZ);
        WriteFloat(writer, slot.ViewAngle0); WriteFloat(writer, slot.ViewAngle1); WriteFloat(writer, slot.ViewAngle2); WriteFloat(writer, slot.ViewFov);
        WriteFloat(writer, slot.RailAngle0); WriteFloat(writer, slot.RailAngle1);
        writer.WriteLine();
    }

    private static void WriteFloat(StreamWriter writer, float value)
    {
        writer.Write(',');
        writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private void LogReadyState(string hook)
    {
        _logger.WriteLine($"[P3R CamFix] Experimental spline {hook} hook active.");
        if (_operationHookReady && _interpolatorHookReady && _splineUpdateHookReady)
            _logger.WriteLine("[P3R CamFix] Experimental spline-camera vNext iteration 12 (recenter edge hardening) is ready.");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _pendingMouseX, 0);
        Interlocked.Exchange(ref _pendingMouseY, 0);
        Interlocked.Exchange(ref _pendingMouseFirstQpc, 0);
        Interlocked.Exchange(ref _pendingMouseLastQpc, 0);
        _traceFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_traceWriter != null)
        {
            lock (_traceWriterLock)
            {
                if (!_traceDisposed)
                {
                    DrainTraceLocked();
                    _traceDisposed = true;
                    _traceWriter.WriteLine($"# captured={_traceRead.ToString(CultureInfo.InvariantCulture)}");
                    _traceWriter.WriteLine($"# dropped={Volatile.Read(ref _traceDropped).ToString(CultureInfo.InvariantCulture)}");
                    _traceWriter.Dispose();
                }
            }
        }
        _traceFlushTimer?.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PeekMessageWDelegate(nint messagePointer, nint window, uint filterMin, uint filterMax, uint removeFlags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetCursorPosDelegate(int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SetCursorDelegate(nint cursor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ShowCursorDelegate(int show);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint userIndex, nint statePointer);

    private delegate void OperationTickDelegate(nint operation, float deltaTime);
    private delegate void SplineInterpolatorDelegate(nint state, nint input, float deltaTime);
    private delegate byte SplineUpdateDelegate(nint hit, float deltaTime, nint sourceTransform, nint outputTransform);

    private enum InputDevice { Unknown, Mouse, Controller }
    private enum MouseSource { None, Raw, Legacy, LegacyDeferred, WarpRejected }
    private enum RecenterReason { None, NativeLock, MovingIdle }

    private struct TraceSlot
    {
        public int Ready, Sequence, OperationSequence, DeviceSwitch;
        public int OperatorKeyState, OperatorState, OperatorNextState, CameraLock, ShowMouseCursor, CameraInputFrozen;
        public int RecenterCancelled, RailMotionActive;
        public long Qpc;
        public nint KernelInput, DefaultInputComponent, CurrentInputComponent, PlayerInput;
        public nint Hit;
        public InputDevice Device;
        public MouseSource MouseSource;
        public RecenterReason RecenterReason;
        public int MouseX, MouseY, WarpRejected, WarpX, WarpY;
        public int CursorVisible, CursorX, CursorY;
        public nint CursorHandle;
        public int WmSetCursor, SetCursorCalls, SetCursorZeroCalls, SetCursorNonzeroCalls, SetCursorSuppressedCalls;
        public nint LastSetCursorHandle;
        public int ShowCursorCalls, LastShowCursorShow, LastShowCursorResult;
        public int WarpCandidateArmed, WarpCandidateX, WarpCandidateY;
        public int StickX, StickY;
        public float RawFirstAgeMs, RawLastAgeMs, OperationToCameraMs, NativeDisableAgeMs;
        public float RecenterProgress, MouseIdleSeconds, RailPositionDelta, RailAngleDelta;
        public float LastSetCursorAgeMs, LastShowCursorAgeMs, WarpCandidateAgeMs;
        public float DeltaTime, NativeX, NativeY, MarginYaw, MarginPitch;
        public float DesiredX, DesiredY, OutputBeforeX, OutputBeforeY, OutputAfterX, OutputAfterY;
        public float DegreesBeforePitch, DegreesBeforeYaw, DegreesAfterPitch, DegreesAfterYaw;
        public float ViewX, ViewY, ViewZ, ViewAngle0, ViewAngle1, ViewAngle2, ViewFov, RailAngle0, RailAngle1;
    }

    private static float ReadFloat(nint address, int offset) => address == 0 ? float.NaN : *(float*)(address + offset);

    private void CaptureNativeInputOwnership(nint operation)
    {
        nint holder = operation == 0 ? 0 : *(nint*)(operation + 0xB0);
        if (holder == 0)
            return;

        _frameOperatorKeyState = *(int*)(holder + 0x280);
        _frameOperatorState = *(int*)(holder + 0x284);
        _frameOperatorNextState = *(int*)(holder + 0x288);
        _frameCameraLock = *(byte*)(operation + 0xC0);
        int previousKeyState = Volatile.Read(ref _lastOperatorKeyState);
        long ownershipQpc = Stopwatch.GetTimestamp();
        if (previousKeyState == 3 && _frameOperatorKeyState != 3)
            Volatile.Write(ref _lastNativeInputDisabledQpc, ownershipQpc);
        Volatile.Write(ref _lastOperatorKeyState, _frameOperatorKeyState);
        Volatile.Write(ref _lastNativeOwnershipQpc, ownershipQpc);
        if (_frameOperatorKeyState == 3)
            Volatile.Write(ref _splineInputEnabledSinceResume, 1);
        nint kernelInput = *(nint*)(holder + 0x268);
        _frameKernelInput = kernelInput;
        if (kernelInput == 0)
            return;

        _frameDefaultInputComponent = *(nint*)(kernelInput + 0x580);
        _frameCurrentInputComponent = *(nint*)(kernelInput + 0x588);
        _frameShowMouseCursor = (*(byte*)(kernelInput + 0x448) & 1) != 0 ? 1 : 0;
        _framePlayerInput = *(nint*)(kernelInput + 0x348);
    }

    private float CursorWarpCandidateAgeMilliseconds()
    {
        long candidate = Volatile.Read(ref _warpCandidateQpc);
        if (candidate == 0) return float.NaN;
        return (float)((Stopwatch.GetTimestamp() - candidate) * 1000.0 / Stopwatch.Frequency);
    }

    private static float QpcAgeMilliseconds(long now, long timestamp)
    {
        if (timestamp == 0 || now < timestamp) return float.NaN;
        return (float)((now - timestamp) * 1000.0 / Stopwatch.Frequency);
    }

    private void MarkSplineGameplayActive(float deltaTime)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref _lastSplineUpdateQpc);
        Volatile.Write(ref _lastSplineUpdateQpc, now);
        Volatile.Write(ref _lastSplineDeltaTime, deltaTime);

        // GetCursorInfo can still report a stale visible arrow before P3R
        // resumes its per-frame SetCursor calls. Hide it as soon as an active
        // spline camera returns after a load gap; later requests still pass
        // through the normal guarded SetCursor hook.
        if (deltaTime > 0.000001f &&
            (previous == 0 || now - previous > Stopwatch.Frequency / 2) &&
            Mod.Configuration.EnableSplineGameplayCursorGuard &&
            _setCursorHook != null)
        {
            Volatile.Write(ref _splineInputEnabledSinceResume, 0);
            _setCursorHook.OriginalFunction(0);
        }
    }

    private bool ShouldSuppressGameplayCursor(long now)
    {
        if (!Mod.Configuration.EnableSplineGameplayCursorGuard)
            return false;
        long recentSpline = Volatile.Read(ref _lastSplineUpdateQpc);
        if (recentSpline == 0 || now - recentSpline < 0 || now - recentSpline > Stopwatch.Frequency / 4)
            return false;
        if (Volatile.Read(ref _lastSplineDeltaTime) <= 0.000001f)
            return false;

        // Cursor ownership is intentionally separate from camera ownership.
        // Hide P3R's erroneous arrow only during native enabled gameplay, or
        // throughout a load-resume interval before gameplay has enabled once.
        long ownership = Volatile.Read(ref _lastNativeOwnershipQpc);
        if (ownership == 0 || now - ownership < 0 || now - ownership > Stopwatch.Frequency / 4)
            return true;
        float nativeLockGrace = Math.Clamp(Mod.Configuration.SplineCursorNativeLockGraceSeconds, 0f, 2f);
        if (IsWithinQpcGrace(now, Volatile.Read(ref _lastNativeInputDisabledQpc), nativeLockGrace))
            return true;
        return Volatile.Read(ref _lastOperatorKeyState) == 3 ||
               Volatile.Read(ref _splineInputEnabledSinceResume) == 0;
    }

    private static bool IsWithinQpcGrace(long now, long start, float seconds)
    {
        if (start == 0 || now < start || seconds <= 0f)
            return false;
        return now - start <= Stopwatch.Frequency * (double)seconds;
    }

    private void CaptureCursorState()
    {
        CursorInfo info = new() { Size = (uint)sizeof(CursorInfo) };
        if (Native.GetCursorInfo(&info) == 0)
        {
            _cursorVisible = -1;
            _cursorX = 0;
            _cursorY = 0;
            _cursorHandle = 0;
            return;
        }

        _cursorVisible = (info.Flags & 1) != 0 ? 1 : 0;
        _cursorX = info.ScreenPosition.X;
        _cursorY = info.ScreenPosition.Y;
        _cursorHandle = info.Cursor;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern nint GetProcAddress(nint module, string procName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(nint rawInput, uint command, void* data, uint* size, uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRegisteredRawInputDevices(RawInputDevice* devices, uint* count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int RegisterRawInputDevices(RawInputDevice* devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetCursorPos(Point* point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetCursorInfo(CursorInfo* cursorInfo);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint Size;
        public uint Flags;
        public nint Cursor;
        public Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }
}

internal readonly record struct FreeCameraInputSnapshot(
    int OperationSequence,
    long OperationQpc,
    int OperatorKeyState,
    int CameraLock,
    int Device,
    bool DeviceChanged,
    int MouseSource,
    int MouseX,
    int MouseY,
    long RawFirstQpc,
    long RawLastQpc,
    int StickX,
    int StickY,
    bool DirectControllerAvailable);

using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Responsive replacement for both spline-camera filters: the 166.7 ms
/// input-target state machine and the subsequent constant 20-degrees/second
/// vector follower. All subclass writes are guarded by the exact live
/// AFldCameraHitSpline vtable for the supported executable.
/// </summary>
internal sealed unsafe class ExperimentalSplineCamera : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint WmSetCursor = 0x0020;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint PmRemove = 0x0001;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RawInputHeaderSizeX64 = 24;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort MouseUsage = 0x0002;
    private const int RawMouseStaleOperationThreshold = 30;
    private const int RawMouseRegistrationCheckInterval = 300;
    private const int VkPageUp = 0x21;
    private const int FldCameraHitSplineVtableRva = 0x4294058;
    private const int FldCameraFreeVtableRva = 0x42901B0;
    private const int FldCameraHitBoxVtableRva = 0x42939A8;
    private const int SplineStateOffset = 0x2A8;
    private const int FldOperatorStateFree = 1;
    private const int FldOperatorStateMax = 15;
    private const float RailMotionHoldDuration = 0.25f;
    private const float RailPositionSpeedThreshold = 5.0f;
    private const float RailAngularSpeedThreshold = 0.5f;
    private const int CursorWarpMinimumCounts = 128;
    private const int CursorWarpMatchTolerance = 8;
    private const float NativeFadeBoundaryBridgeSeconds = 0.075f;
    private const float MouseRecoveryYawSpeed = 150f;
    private const float MouseRecoveryPitchSpeed = 90f;
    private const float MouseRecoveryRecentRawGraceSeconds = 0.25f;

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
    private readonly bool _diagnosticsEnabled;
    private readonly bool _cameraInputTraceEnabled;
    private readonly CameraTransitionTrace? _transitionTrace;
    private readonly TraceMarkerRecorder? _traceMarker;
    private readonly UnrealFadeProbe? _fadeProbe;
    private readonly GamepadInputRouter? _gamepadInput;
    private readonly IHook<PeekMessageWDelegate>? _peekMessageHook;
    private readonly IHook<SetCursorPosDelegate>? _setCursorPosHook;
    private readonly IHook<SetCursorDelegate>? _setCursorHook;
    private readonly IHook<ShowCursorDelegate>? _showCursorHook;
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
    private nint _lastSetCursorApplied;
    private long _lastSetCursorQpc;
    private int _lastShowCursorShow;
    private int _lastShowCursorResult;
    private long _lastShowCursorQpc;
    private long _lastSplineUpdateQpc;
    private float _lastSplineDeltaTime;
    private long _lastNativeOwnershipQpc;
    private long _lastNativeInputDisabledQpc;
    private long _lastFreeOperationQpc;
    private long _lastFreeNativeInputDisabledQpc;
    private long _lastNativeFadeActiveQpc;
    private long _lastAcceptedRawMouseQpc;
    private int _lastOperatorKeyState;
    private int _lastOperatorState;
    private int _lastOperatorNextState;
    private int _lastCameraLock;
    private int _lastFreeOperatorKeyState;
    private int _lastFadeProbeStatus;
    private int _lastFadeMode;
    private int _nativeFadeTransactionActive;
    private int _traceMarkerKeyDown;
    private float _lastFreeDeltaTime;
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
    private int _directControllerAvailable;
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
    private bool _pendingSplineFrameReady;
    private PendingSplineFrame _pendingSplineFrame;

    private readonly TraceSlot[]? _traceSlots;
    private readonly StreamWriter? _traceWriter;
    private readonly Timer? _traceFlushTimer;
    private readonly object? _traceWriterLock;
    private int _traceReserved;
    private int _traceRead;
    private int _traceDropped;
    private bool _traceDisposed;

    public ExperimentalSplineCamera(ModContext context, nint imageBase)
    {
        _logger = context.Logger;
        _hooks = context.Hooks!;
        _imageBase = imageBase;
        _diagnosticsEnabled = Mod.Configuration.EnableCameraTransitionTrace ||
                              Mod.Configuration.EnableSplineCameraTrace;
        _cameraInputTraceEnabled = _diagnosticsEnabled ||
                                   Mod.Configuration.EnableFreeCameraTrace;

        if (Mod.Configuration.EnableDirectController)
            _gamepadInput = new GamepadInputRouter(_logger);

        if (Mod.Configuration.EnableCameraTransitionTrace ||
            Mod.Configuration.EnableFreeCameraFix || Mod.Configuration.EnableSplineCameraFix)
            _fadeProbe = new UnrealFadeProbe(context, imageBase);

        if (Mod.Configuration.EnableCameraTransitionTrace)
            _transitionTrace = new CameraTransitionTrace(context, imageBase);

        if (_diagnosticsEnabled)
            _traceMarker = new TraceMarkerRecorder(context);

        if (Mod.Configuration.EnableSplineCameraTrace)
        {
            _traceWriterLock = new object();
            int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
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
            _logger.WriteLine($"[P3R CamFix] Spline-camera debug trace active: {tracePath}");
        }

        try
        {
            nint user32 = Native.GetModuleHandleW("user32.dll");
            nint peekMessage = user32 == 0 ? 0 : Native.GetProcAddress(user32, "PeekMessageW");
            if (peekMessage == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32!PeekMessageW");

            _peekMessageHook = _hooks.CreateHook<PeekMessageWDelegate>(PeekMessageW, peekMessage);
            _peekMessageHook.Activate();
            _logger.WriteLine("[P3R CamFix] Raw-mouse camera input active.");
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
            _logger.WriteLine("[P3R CamFix] Cursor-warp rejection active.");
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Cursor-warp correlation unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        try
        {
            nint user32 = Native.GetModuleHandleW("user32.dll");
            nint setCursor = user32 == 0 ? 0 : Native.GetProcAddress(user32, "SetCursor");
            if (setCursor == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32!SetCursor");

            _setCursorHook = _hooks.CreateHook<SetCursorDelegate>(SetCursor, setCursor);
            _setCursorHook.Activate();
            _logger.WriteLine("[P3R CamFix] Native cursor ownership guard active.");

            if (_diagnosticsEnabled)
            {
                nint showCursor = Native.GetProcAddress(user32, "ShowCursor");
                if (showCursor == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve user32!ShowCursor");
                _showCursorHook = _hooks.CreateHook<ShowCursorDelegate>(ShowCursor, showCursor);
                _showCursorHook.Activate();
            }
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[P3R CamFix] Cursor-state call tracing unavailable: {exception.Message}", System.Drawing.Color.Orange);
        }

        context.StartupScanner.AddMainModuleScan(OperationTickSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Field-camera operation signature not found; direct camera input disabled.", System.Drawing.Color.Red);
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
                _logger.WriteLine("[P3R CamFix] Spline-camera interpolator signature not found; spline fix disabled.", System.Drawing.Color.Red);
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
                _logger.WriteLine("[P3R CamFix] Spline-camera update signature not found; moving-idle recenter unavailable.", System.Drawing.Color.Orange);
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
        if (_traceMarker != null)
        {
            nint virtualKey = *(nint*)(messagePointer + 0x10);
            if (virtualKey == VkPageUp &&
                (message == WmKeyUp || message == WmSysKeyUp))
            {
                Volatile.Write(ref _traceMarkerKeyDown, 0);
            }
            else if (virtualKey == VkPageUp &&
                     (message == WmKeyDown || message == WmSysKeyDown) &&
                     (((ulong)*(nint*)(messagePointer + 0x18) >> 30) & 1UL) == 0 &&
                     Interlocked.Exchange(ref _traceMarkerKeyDown, 1) == 0)
            {
                CaptureTraceMarker();
            }
        }
        if (_diagnosticsEnabled && message == WmSetCursor)
            Interlocked.Increment(ref _pendingWmSetCursor);
        if (message != WmInput)
            return result;

        nint rawHandle = *(nint*)(messagePointer + 0x18);
        uint size = 256;
        byte* buffer = stackalloc byte[(int)size];
        uint read = Native.GetRawInputData(rawHandle, RidInput, buffer, &size, RawInputHeaderSizeX64);
        if (read < 44 || read == uint.MaxValue || *(uint*)buffer != RimTypeMouse ||
            (*(ushort*)(buffer + 0x18) & MouseMoveAbsolute) != 0)
            return result;

        int x = *(int*)(buffer + 0x24);
        int y = *(int*)(buffer + 0x28);
        if (IsRecentCursorWarpPacket(x, y))
        {
            if (_diagnosticsEnabled)
            {
                Interlocked.Increment(ref _pendingWarpRejected);
                Volatile.Write(ref _pendingWarpX, x);
                Volatile.Write(ref _pendingWarpY, y);
            }
            return result;
        }
        if (x != 0) Interlocked.Add(ref _pendingMouseX, x);
        if (y != 0) Interlocked.Add(ref _pendingMouseY, y);
        if (x != 0 || y != 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cameraInputTraceEnabled)
            {
                Interlocked.CompareExchange(ref _pendingMouseFirstQpc, now, 0);
                Volatile.Write(ref _pendingMouseLastQpc, now);
            }
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
        if (result == 0 || haveBefore == 0)
            return result;

        int deltaX = x - before.X;
        int deltaY = y - before.Y;
        if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < CursorWarpMinimumCounts)
            return result;

        Volatile.Write(ref _warpCandidateX, deltaX);
        Volatile.Write(ref _warpCandidateY, deltaY);
        Volatile.Write(ref _warpCandidateQpc, Stopwatch.GetTimestamp());
        Volatile.Write(ref _warpCandidateArmed, 1);
        return result;
    }

    private void CaptureTraceMarker()
    {
        long qpc = Stopwatch.GetTimestamp();
        _traceMarker?.Capture(new TraceMarkerSnapshot(
            qpc,
            Volatile.Read(ref _operationSequence),
            Volatile.Read(ref _lastOperatorKeyState),
            Volatile.Read(ref _lastOperatorState),
            Volatile.Read(ref _lastOperatorNextState),
            Volatile.Read(ref _lastCameraLock),
            Volatile.Read(ref _activeDevice),
            _cursorVisible,
            _cursorHandle,
            Volatile.Read(ref _lastSetCursorRequested),
            Volatile.Read(ref _lastSetCursorApplied),
            Volatile.Read(ref _lastSetCursorQpc),
            Volatile.Read(ref _lastFadeProbeStatus),
            Volatile.Read(ref _lastFadeMode),
            Volatile.Read(ref _nativeFadeTransactionActive)));
    }

    private nint SetCursor(nint cursor)
    {
        long now = Stopwatch.GetTimestamp();
        nint appliedCursor = cursor;
        if (cursor != 0 && ShouldSuppressGameplayCursor(now))
        {
            appliedCursor = 0;
            if (_diagnosticsEnabled)
                Interlocked.Increment(ref _pendingSetCursorSuppressedCalls);
        }

        nint result = _setCursorHook!.OriginalFunction(appliedCursor);
        if (_diagnosticsEnabled)
        {
            Interlocked.Increment(ref _pendingSetCursorCalls);
            if (cursor == 0)
                Interlocked.Increment(ref _pendingSetCursorZeroCalls);
            else
                Interlocked.Increment(ref _pendingSetCursorNonzeroCalls);
            Volatile.Write(ref _lastSetCursorRequested, cursor);
            Volatile.Write(ref _lastSetCursorApplied, appliedCursor);
            Volatile.Write(ref _lastSetCursorQpc, now);
        }
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
        if (Volatile.Read(ref _warpCandidateArmed) == 0)
            return false;

        long age = Stopwatch.GetTimestamp() - Volatile.Read(ref _warpCandidateQpc);
        if (age < 0 || age > Stopwatch.Frequency / 10)
        {
            Volatile.Write(ref _warpCandidateArmed, 0);
            return false;
        }

        int candidateX = Volatile.Read(ref _warpCandidateX);
        int candidateY = Volatile.Read(ref _warpCandidateY);
        if (!MatchesCursorWarpPacket(x, y, candidateX, candidateY, CursorWarpMatchTolerance))
            return false;

        Volatile.Write(ref _warpCandidateArmed, 0);
        return true;
    }

    private static bool MatchesCursorWarpPacket(int packetX, int packetY, int warpX, int warpY, int tolerance) =>
        Math.Abs((long)packetX - warpX) <= tolerance && Math.Abs((long)packetY - warpY) <= tolerance;

    private void OperationTick(nint operation, float deltaTime)
    {
        _operationSequence++;
        _frameOperationQpc = Stopwatch.GetTimestamp();
        CaptureNativeInputOwnership(operation);
        if (_traceMarker != null)
            PollTraceMarkerKey();
        FadeRuntimeSnapshot fade =
            _fadeProbe?.CaptureRuntime(_transitionTrace != null) ?? default;
        CaptureFadeCursorState(operation, deltaTime, fade);
        _frameMouseX = Interlocked.Exchange(ref _pendingMouseX, 0);
        _frameMouseY = Interlocked.Exchange(ref _pendingMouseY, 0);
        if (_cameraInputTraceEnabled)
        {
            _frameMouseFirstQpc = Interlocked.Exchange(ref _pendingMouseFirstQpc, 0);
            _frameMouseLastQpc = Interlocked.Exchange(ref _pendingMouseLastQpc, 0);
        }
        if (_diagnosticsEnabled)
        {
            _frameWarpRejected = Interlocked.Exchange(ref _pendingWarpRejected, 0);
            _frameWarpX = Interlocked.Exchange(ref _pendingWarpX, 0);
            _frameWarpY = Interlocked.Exchange(ref _pendingWarpY, 0);
            _frameWmSetCursor = Interlocked.Exchange(ref _pendingWmSetCursor, 0);
            _frameSetCursorCalls = Interlocked.Exchange(ref _pendingSetCursorCalls, 0);
            _frameSetCursorZeroCalls = Interlocked.Exchange(ref _pendingSetCursorZeroCalls, 0);
            _frameSetCursorNonzeroCalls = Interlocked.Exchange(ref _pendingSetCursorNonzeroCalls, 0);
            _frameSetCursorSuppressedCalls = Interlocked.Exchange(ref _pendingSetCursorSuppressedCalls, 0);
            _frameShowCursorCalls = Interlocked.Exchange(ref _pendingShowCursorCalls, 0);
        }
        _frameMouseSource = (_frameMouseX != 0 || _frameMouseY != 0)
            ? MouseSource.Raw
            : _diagnosticsEnabled && _frameWarpRejected != 0
                ? MouseSource.WarpRejected
                : MouseSource.None;
        if (_diagnosticsEnabled)
            CaptureCursorState();

        if (AnyRawMouseEnabled() &&
            _operationSequence - Volatile.Read(ref _lastRawInputOperation) > RawMouseStaleOperationThreshold &&
            (_lastRegistrationCheckOperation == 0 ||
             _operationSequence - _lastRegistrationCheckOperation >= RawMouseRegistrationCheckInterval))
        {
            _lastRegistrationCheckOperation = _operationSequence;
            EnsureRawMouseRegistration();
        }

        float configuredDeadzone = Math.Clamp(Mod.Configuration.GamepadDeadzonePercent, 0, 50) / 100f;
        float switchThreshold = Math.Max(0.02f, Math.Clamp(configuredDeadzone, 0f, 0.95f) * 0.5f);
        DirectGamepadSnapshot direct = Mod.Configuration.EnableDirectController
            ? _gamepadInput?.Poll(switchThreshold) ?? default
            : default;
        Volatile.Write(ref _rightStickX, direct.RightStickX);
        Volatile.Write(ref _rightStickY, direct.RightStickY);
        Volatile.Write(ref _directControllerAvailable, direct.Available ? 1 : 0);
        int stickX = Volatile.Read(ref _rightStickX);
        int stickY = Volatile.Read(ref _rightStickY);
        InputDevice previous = (InputDevice)Volatile.Read(ref _activeDevice);
        InputDevice next = previous;

        if (AnyRawMouseEnabled() && (_frameMouseX != 0 || _frameMouseY != 0))
        {
            next = InputDevice.Mouse;
        }
        else if (direct.Available)
        {
            float x = NormalizeStick(stickX);
            float y = NormalizeStick(stickY);
            float deltaX = NormalizeStick(stickX - _lastOperationStickX);
            float deltaY = NormalizeStick(stickY - _lastOperationStickY);
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
            _logger.WriteLine($"[P3R CamFix] Camera input device: {previous} -> {next}.");
        }
        _lastOperationStickX = stickX;
        _lastOperationStickY = stickY;

        if (_transitionTrace != null)
            InvokeOperationWithTransitionTrace(operation, deltaTime, next);
        else
            _operationTickHook!.OriginalFunction(operation, deltaTime);

        _frameMouseX = 0;
        _frameMouseY = 0;
        if (_cameraInputTraceEnabled)
        {
            _frameMouseFirstQpc = 0;
            _frameMouseLastQpc = 0;
            _frameCameraLock = 0;
        }
        _frameOperationQpc = 0;
        _frameOperatorKeyState = 0;
        _frameOperatorState = 0;
        if (_diagnosticsEnabled)
        {
            _frameOperatorNextState = 0;
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
        }
        _frameMouseSource = MouseSource.None;
        _mouseBecameActive = false;
        _deviceChangedThisFrame = false;
    }

    private void InvokeOperationWithTransitionTrace(
        nint operation,
        float deltaTime,
        InputDevice device)
    {
        CameraTransitionTrace trace = _transitionTrace!;
        FadeSnapshot fade = _fadeProbe?.CaptureDiagnostics() ?? default;
        var transition = new CameraTransitionObservation
        {
            OperationSequence = _operationSequence,
            QpcEnter = _frameOperationQpc,
            Operation = operation,
            DeltaTime = deltaTime,
            IdentityBefore = trace.CaptureIdentity(operation),
            KernelInput = _frameKernelInput,
            DefaultInputComponent = _frameDefaultInputComponent,
            CurrentInputComponent = _frameCurrentInputComponent,
            ShowMouseCursor = _frameShowMouseCursor,
            Device = (int)device,
            DeviceSwitch = _deviceChangedThisFrame ? 1 : 0,
            MouseSource = (int)_frameMouseSource,
            MouseX = _frameMouseX,
            MouseY = _frameMouseY,
            RawFirstQpc = _frameMouseFirstQpc,
            RawLastQpc = _frameMouseLastQpc,
            CursorVisibleBefore = _cursorVisible,
            CursorXBefore = _cursorX,
            CursorYBefore = _cursorY,
            CursorHandleBefore = _cursorHandle,
            WmSetCursorPrevious = _frameWmSetCursor,
            SetCursorPrevious = _frameSetCursorCalls,
            SetCursorZeroPrevious = _frameSetCursorZeroCalls,
            SetCursorNonzeroPrevious = _frameSetCursorNonzeroCalls,
            SetCursorSuppressedPrevious = _frameSetCursorSuppressedCalls,
            ShowCursorPrevious = _frameShowCursorCalls,
            MessageWindow = Volatile.Read(ref _messageWindow),
        };

        _operationTickHook!.OriginalFunction(operation, deltaTime);

        transition.QpcExit = Stopwatch.GetTimestamp();
        CaptureCursorState();
        transition.CursorVisibleAfter = _cursorVisible;
        transition.CursorXAfter = _cursorX;
        transition.CursorYAfter = _cursorY;
        transition.CursorHandleAfter = _cursorHandle;
        transition.SetCursorPendingAfter = Volatile.Read(ref _pendingSetCursorCalls);
        transition.SetCursorZeroPendingAfter = Volatile.Read(ref _pendingSetCursorZeroCalls);
        transition.SetCursorNonzeroPendingAfter = Volatile.Read(ref _pendingSetCursorNonzeroCalls);
        transition.SetCursorSuppressedPendingAfter = Volatile.Read(ref _pendingSetCursorSuppressedCalls);
        transition.LastSetCursorRequested = Volatile.Read(ref _lastSetCursorRequested);
        transition.LastSetCursorApplied = Volatile.Read(ref _lastSetCursorApplied);
        transition.LastSetCursorQpc = Volatile.Read(ref _lastSetCursorQpc);
        transition.ShowCursorPendingAfter = Volatile.Read(ref _pendingShowCursorCalls);
        transition.LastShowCursorShow = Volatile.Read(ref _lastShowCursorShow);
        transition.LastShowCursorResult = Volatile.Read(ref _lastShowCursorResult);
        transition.LastShowCursorQpc = Volatile.Read(ref _lastShowCursorQpc);
        trace.Capture(transition, fade);
    }

    private void PollTraceMarkerKey()
    {
        bool down = (Native.GetAsyncKeyState(VkPageUp) & 0x8000) != 0;
        int previous = Interlocked.Exchange(ref _traceMarkerKeyDown, down ? 1 : 0);
        if (down && previous == 0)
            CaptureTraceMarker();
    }

    private void SplineInterpolator(nint state, nint input, float deltaTime)
    {
        if (!Mod.Configuration.Enabled || !Mod.Configuration.EnableSplineCameraFix || !IsSplineState(state) || input == 0)
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
        InputDevice device = (InputDevice)Volatile.Read(ref _activeDevice);

        // Raw mouse delivery can disappear across menus/maps. P3R's native
        // mouse axis remains distinguishable by its exact 0.006 steps, so use
        // it to restore mouse ownership while the bounded registration check
        // recovers direct raw input.
        if (device != InputDevice.Mouse &&
            IsStickCenteredForDeviceSwitch() &&
            IsQuantizedMouseInput(nativeX, nativeY))
        {
            InputDevice previous = device;
            device = InputDevice.Mouse;
            Volatile.Write(ref _activeDevice, (int)device);
            _mouseBecameActive = true;
            _deviceChangedThisFrame = true;
            _logger.WriteLine($"[P3R CamFix] Camera input device: {previous} -> Mouse (native-axis recovery).");
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
        bool recoveryMouseMovement = device == InputDevice.Mouse &&
                                     _frameMouseSource == MouseSource.None &&
                                     (Math.Abs(nativeX) > 0.0001f || Math.Abs(nativeY) > 0.0001f);
        if (recoveryMouseMovement)
        {
            float rawAgeSeconds = QpcAgeSeconds(Stopwatch.GetTimestamp(), Volatile.Read(ref _lastAcceptedRawMouseQpc));
            if (ShouldDeferMouseRecovery(
                    _recenterReason == RecenterReason.NativeLock,
                    _recenterProgress,
                    rawAgeSeconds))
            {
                recoveryMouseMovement = false;
                _frameMouseSource = MouseSource.RecoveryDeferred;
            }
            else
            {
                _frameMouseSource = MouseSource.NativeAxisRecovery;
            }
        }
        bool acceptedMouseMovement = rawMouseMovement || recoveryMouseMovement;

        (float controllerDemandX, float controllerDemandY) =
            device == InputDevice.Controller &&
            Mod.Configuration.EnableDirectController &&
            Volatile.Read(ref _directControllerAvailable) != 0
                ? ApplyControllerCurve(Volatile.Read(ref _rightStickX), Volatile.Read(ref _rightStickY))
                : (Math.Clamp(nativeX, -1f, 1f), Math.Clamp(nativeY, -1f, 1f));
        bool acceptedCameraDemand = device == InputDevice.Mouse && Mod.Configuration.EnableRawMouse
            ? acceptedMouseMovement
            : (controllerDemandX * controllerDemandX) + (controllerDemandY * controllerDemandY) > 0.000001f;

        _recenterCancelledThisFrame = false;
        if (!cameraInputFrozen && device == InputDevice.Mouse && Mod.Configuration.EnableRawMouse)
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
            Mod.Configuration.EnableRawMouse &&
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
        else if (device == InputDevice.Mouse && Mod.Configuration.EnableRawMouse)
        {
            desiredX = (_mouseBecameActive || _recenterCancelledThisFrame) ? outputBeforeX : ClampFinite(*(float*)(state + 0x18));
            desiredY = (_mouseBecameActive || _recenterCancelledThisFrame) ? outputBeforeY : ClampFinite(*(float*)(state + 0x1C));
            float splineMouseScale = Math.Clamp(Mod.Configuration.SplineMouseSensitivityPercent, 0, 200) / 100f;
            float yawSensitivity = 0.04f * Math.Clamp(Mod.Configuration.MouseHorizontalSensitivityPercent, 10, 300) / 100f * splineMouseScale;
            float pitchSensitivity = 0.03f * Math.Clamp(Mod.Configuration.MouseVerticalSensitivityPercent, 10, 300) / 100f * splineMouseScale;
            float rawYawDirection = Mod.Configuration.InvertMouseX ? -1f : 1f;
            float rawPitchDirection = Mod.Configuration.InvertMouseY ? 1f : -1f;
            desiredX = Math.Clamp(desiredX + (_frameMouseX * yawSensitivity * rawYawDirection / marginYaw), -1f, 1f);
            desiredY = Math.Clamp(desiredY + (_frameMouseY * pitchSensitivity * rawPitchDirection / marginPitch), -1f, 1f);

            if (_frameMouseSource == MouseSource.NativeAxisRecovery)
            {
                float dt = Math.Clamp(deltaTime, 0f, 0.1f);
                // This value has already passed through P3R's input pipeline,
                // including its native axis inversion. Preserve that sign;
                // mod inversion belongs only to direct raw mouse counts.
                desiredX = Math.Clamp(desiredX + (nativeX * MouseRecoveryYawSpeed * dt / marginYaw), -1f, 1f);
                desiredY = Math.Clamp(desiredY + (nativeY * MouseRecoveryPitchSpeed * dt / marginPitch), -1f, 1f);
                _frameMouseSource = MouseSource.NativeAxisRecovery;
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
            float candidateX = Lerp(outputBeforeX, desiredX, alpha);
            float candidateY = Lerp(outputBeforeY, desiredY, alpha);
            // Stick deflection remains the full normalized angle target. Apply
            // sensitivity only as a degree-space rate cap so it cannot shrink
            // the authored yaw or pitch range.
            (outputX, outputY) = LimitControllerTurnRate(
                deltaTime, outputBeforeX, outputBeforeY, candidateX, candidateY, marginYaw, marginPitch);
        }

        outputX = ClampFinite(outputX);
        outputY = ClampFinite(outputY);
        WriteReplacementState(state, desiredX, desiredY, outputX, outputY);

        // +0x11035F0 would otherwise chase this output at a fixed 20 deg/s.
        // Seed its current vector to the same value so it contributes no second
        // filter; our single FPS-independent response above owns the motion.
        WriteVector(hit + 0x29C, 0f, outputY * marginPitch, outputX * marginYaw);

        if (_splineUpdateHookReady)
        {
            _pendingSplineFrame = new PendingSplineFrame
            {
                Hit = hit,
                OperationSequence = _operationSequence,
                DegreesAfterYaw = outputX * marginYaw,
                DegreesAfterPitch = outputY * marginPitch,
            };
            _pendingSplineFrameReady = true;
        }

        if (_traceSlots == null)
            return;

        float degreesBeforePitch = *(float*)(hit + 0x2A0);
        float degreesBeforeYaw = *(float*)(hit + 0x2A4);
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
        _pendingSplineFrameReady = false;
        if (_traceSlots != null)
            _pendingTraceReady = false;
        byte result = _splineUpdateHook!.OriginalFunction(hit, deltaTime, sourceTransform, outputTransform);

        bool matchingFrame = _pendingSplineFrameReady &&
                             _pendingSplineFrame.Hit == hit &&
                             _pendingSplineFrame.OperationSequence == _operationSequence;
        if (matchingFrame && outputTransform != 0)
        {
            float viewX = ReadFloat(outputTransform, 0x00);
            float viewY = ReadFloat(outputTransform, 0x04);
            float viewZ = ReadFloat(outputTransform, 0x08);
            float viewAngle0 = ReadFloat(outputTransform, 0x0C);
            float viewAngle1 = ReadFloat(outputTransform, 0x10);
            float railAngle0 = viewAngle0 - _pendingSplineFrame.DegreesAfterYaw;
            float railAngle1 = viewAngle1 - _pendingSplineFrame.DegreesAfterPitch;
            UpdateRailMotion(hit, deltaTime, viewX, viewY, viewZ, railAngle0, railAngle1);
        }

        if (_traceSlots != null && _pendingTraceReady &&
            _pendingTrace.Hit == hit && _pendingTrace.OperationSequence == _operationSequence)
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
                trace.RailPositionDelta = _lastRailPositionDelta;
                trace.RailAngleDelta = _lastRailAngleDelta;
            }
            ReserveTrace(trace);
        }
        _pendingSplineFrameReady = false;
        if (_traceSlots != null)
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

    private static (float X, float Y) LimitControllerTurnRate(
        float deltaTime,
        float outputX,
        float outputY,
        float candidateX,
        float candidateY,
        float marginYaw,
        float marginPitch)
    {
        float dt = Math.Clamp(deltaTime, 0f, 0.1f);
        float splineSpeed = Math.Clamp(Mod.Configuration.SplineGamepadSensitivityPercent, 0, 200) / 100f;
        float yawDelta = (candidateX - outputX) * marginYaw;
        float pitchDelta = (candidateY - outputY) * marginPitch;
        float yawScale = 1f;
        if (Math.Abs(yawDelta) > float.Epsilon)
        {
            float maxYawDelta = Math.Clamp(Mod.Configuration.GamepadHorizontalSpeed, 0, 660) * splineSpeed * dt;
            yawScale = Math.Min(1f, maxYawDelta / Math.Abs(yawDelta));
        }
        float pitchScale = 1f;
        if (Math.Abs(pitchDelta) > float.Epsilon)
        {
            float maxPitchDelta = Math.Clamp(Mod.Configuration.GamepadVerticalSpeed, 0, 400) * splineSpeed * dt;
            pitchScale = Math.Min(1f, maxPitchDelta / Math.Abs(pitchDelta));
        }
        return (Lerp(outputX, candidateX, yawScale), Lerp(outputY, candidateY, pitchScale));
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
        float threshold = Math.Max(0.02f, (Math.Clamp(Mod.Configuration.GamepadDeadzonePercent, 0, 50) / 100f) * 0.5f);
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
            _logger.WriteLine($"[P3R CamFix] Raw-mouse registration restore failed (Win32 {Marshal.GetLastWin32Error()}); raw mouse remains unavailable.", System.Drawing.Color.Orange);
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

    private static bool ShouldDeferMouseRecovery(bool nativeLockRecenterActive, float recenterProgress, float rawAgeSeconds) =>
        nativeLockRecenterActive &&
        recenterProgress < 0.9999f &&
        rawAgeSeconds >= 0f &&
        rawAgeSeconds <= MouseRecoveryRecentRawGraceSeconds;

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

    private static bool AnyRawMouseEnabled() =>
        Mod.Configuration.Enabled && Mod.Configuration.EnableRawMouse &&
        (Mod.Configuration.EnableSplineCameraFix || Mod.Configuration.EnableFreeCameraFix);

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
        Volatile.Read(ref _directControllerAvailable) != 0);
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
        lock (_traceWriterLock!)
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
        _logger.WriteLine($"[P3R CamFix] Spline-camera {hook} hook active.");
        if (_operationHookReady && _interpolatorHookReady && _splineUpdateHookReady)
            _logger.WriteLine("[P3R CamFix] Spline-camera replacement ready.");
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
            lock (_traceWriterLock!)
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
        _transitionTrace?.Dispose();
        _traceMarker?.Dispose();
        _gamepadInput?.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PeekMessageWDelegate(nint messagePointer, nint window, uint filterMin, uint filterMax, uint removeFlags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetCursorPosDelegate(int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SetCursorDelegate(nint cursor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ShowCursorDelegate(int show);

    private delegate void OperationTickDelegate(nint operation, float deltaTime);
    private delegate void SplineInterpolatorDelegate(nint state, nint input, float deltaTime);
    private delegate byte SplineUpdateDelegate(nint hit, float deltaTime, nint sourceTransform, nint outputTransform);

    private enum InputDevice { Unknown, Mouse, Controller }
    private enum MouseSource { None, Raw, NativeAxisRecovery, RecoveryDeferred, WarpRejected }
    private enum RecenterReason { None, NativeLock, MovingIdle }

    private struct PendingSplineFrame
    {
        public int OperationSequence;
        public nint Hit;
        public float DegreesAfterPitch, DegreesAfterYaw;
    }

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
        if (_diagnosticsEnabled)
            _frameOperatorNextState = *(int*)(holder + 0x288);
        if (_cameraInputTraceEnabled)
            _frameCameraLock = *(byte*)(operation + 0xC0);
        int previousKeyState = Volatile.Read(ref _lastOperatorKeyState);
        long ownershipQpc = Stopwatch.GetTimestamp();
        if (previousKeyState == 3 && _frameOperatorKeyState != 3)
            Volatile.Write(ref _lastNativeInputDisabledQpc, ownershipQpc);
        Volatile.Write(ref _lastOperatorKeyState, _frameOperatorKeyState);
        Volatile.Write(ref _lastOperatorState, _frameOperatorState);
        if (_diagnosticsEnabled)
        {
            Volatile.Write(ref _lastOperatorNextState, _frameOperatorNextState);
            Volatile.Write(ref _lastCameraLock, _frameCameraLock);
        }
        Volatile.Write(ref _lastNativeOwnershipQpc, ownershipQpc);
        if (_frameOperatorKeyState == 3)
            Volatile.Write(ref _splineInputEnabledSinceResume, 1);

        if (!_diagnosticsEnabled)
            return;

        nint kernelInput = *(nint*)(holder + 0x268);
        _frameKernelInput = kernelInput;
        if (kernelInput == 0)
            return;

        _frameDefaultInputComponent = *(nint*)(kernelInput + 0x580);
        _frameCurrentInputComponent = *(nint*)(kernelInput + 0x588);
        _frameShowMouseCursor = (*(byte*)(kernelInput + 0x448) & 1) != 0 ? 1 : 0;
        _framePlayerInput = *(nint*)(kernelInput + 0x348);
    }

    private void CaptureFadeCursorState(nint operation, float deltaTime, in FadeRuntimeSnapshot fade)
    {
        Volatile.Write(ref _lastFadeProbeStatus, fade.Status);
        Volatile.Write(ref _lastFadeMode, fade.Mode);

        long now = _frameOperationQpc != 0 ? _frameOperationQpc : Stopwatch.GetTimestamp();
        if (fade.Status == 2 && fade.Mode != 0)
            Volatile.Write(ref _lastNativeFadeActiveQpc, now);

        if (_frameOperatorKeyState == 3)
            Volatile.Write(ref _nativeFadeTransactionActive, 0);
        else if (fade.Status == 2 && fade.Mode != 0)
            Volatile.Write(ref _nativeFadeTransactionActive, 1);

        if (!IsFreeOperationCamera(operation))
            return;

        Volatile.Write(ref _lastFreeOperationQpc, now);
        Volatile.Write(ref _lastFreeDeltaTime, deltaTime);

        int previousKeyState = Volatile.Read(ref _lastFreeOperatorKeyState);
        if (previousKeyState == 3 && _frameOperatorKeyState != 3)
            Volatile.Write(ref _lastFreeNativeInputDisabledQpc, now);
        Volatile.Write(ref _lastFreeOperatorKeyState, _frameOperatorKeyState);

    }

    private bool IsFreeOperationCamera(nint operation)
    {
        if (operation == 0)
            return false;

        nint owner = *(nint*)(operation + 0xB0);
        if (owner == 0)
            return false;

        nint hit = *(nint*)(operation + 0xB8);
        nint camera;
        if (hit == 0)
        {
            camera = *(nint*)(owner + 0x240);
        }
        else if (*(nint*)hit == _imageBase + FldCameraHitBoxVtableRva)
        {
            camera = *(nint*)(hit + 0x2E8);
        }
        else
        {
            return false;
        }

        return camera != 0 && *(nint*)camera == _imageBase + FldCameraFreeVtableRva;
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
            _setCursorHook != null)
        {
            Volatile.Write(ref _splineInputEnabledSinceResume, 0);
            _setCursorHook.OriginalFunction(0);
        }
    }

    private bool ShouldSuppressGameplayCursor(long now)
    {
        if (!Mod.Configuration.Enabled)
            return false;

        int liveFadeMode = 0;
        bool haveLiveFadeMode = _fadeProbe?.TryReadLiveMode(out liveFadeMode) == true;
        bool fadeTransactionActive = Volatile.Read(ref _nativeFadeTransactionActive) != 0;

        // Battle command UI can become interactive while the tail of P3R's
        // glass-transition FadePlayer is still active. At this point two native
        // signals agree: BtlGuiState owns command input, and SetCursor was
        // called with a non-null handle. Let that exact UI owner outrank only
        // our cursor hold; BtlGuiState::None and every non-battle fade retain
        // the normal suppression path below.
        bool liveBattleGuiCursorOwner = false;
        _fadeProbe?.TryReadLiveBattleGuiCursorOwner(out liveBattleGuiCursorOwner);
        if (liveBattleGuiCursorOwner)
            return false;

        if (ShouldSuppressNativeFadeTransactionCursor(
            Volatile.Read(ref _lastFadeProbeStatus),
            fadeTransactionActive,
            haveLiveFadeMode,
            liveFadeMode))
            return true;

        // Message/UI actor traversal is only relevant to a completed fade
        // transaction hold. Ordinary gameplay and active fades never pay for it.
        bool liveNativeUiCursorOwner = false;
        if (fadeTransactionActive)
        {
            bool liveMessageCursorOwner = false;
            bool liveActorUiCursorOwner = false;
            _fadeProbe?.TryReadLiveMessageCursorOwner(out liveMessageCursorOwner);
            _fadeProbe?.TryReadLiveActorUiCursorOwner(out liveActorUiCursorOwner);
            bool liveOperationCursorOwner = HasNativeOperationCursorOwner(
                now,
                Volatile.Read(ref _lastNativeOwnershipQpc),
                Volatile.Read(ref _lastOperatorState));
            liveNativeUiCursorOwner = liveMessageCursorOwner || liveActorUiCursorOwner ||
                                      liveBattleGuiCursorOwner || liveOperationCursorOwner;
        }

        return ShouldSuppressSplineGameplayCursor(now, liveNativeUiCursorOwner) ||
               ShouldSuppressFreeFadeCursor(now, liveNativeUiCursorOwner);
    }

    private static bool ShouldSuppressNativeFadeTransactionCursor(
        int fadeProbeStatus, bool transactionActive,
        bool liveFadeModeAvailable, int liveFadeMode) =>
        fadeProbeStatus == 2 && transactionActive &&
        liveFadeModeAvailable && liveFadeMode != 0;

    private static bool HasNativeOperationCursorOwner(
        long now, long lastOwnershipQpc, int operatorState)
    {
        // EFldOperatorState::Free is ordinary field gameplay and the state used
        // by loading holds. Every higher in-range value is a named native modal
        // operation (menus, map, battle, event, save, and so on). This method is
        // consulted only for a nonzero SetCursor request after the active fade
        // has ended, so it cannot create a cursor by itself. Require a fresh
        // operation tick to avoid carrying a modal state into title teardown.
        if (lastOwnershipQpc == 0 || now < lastOwnershipQpc ||
            now - lastOwnershipQpc > Stopwatch.Frequency / 4)
            return false;

        return operatorState > FldOperatorStateFree && operatorState < FldOperatorStateMax;
    }

    private bool ShouldSuppressSplineGameplayCursor(long now, bool nativeUiCursorOwner)
    {
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
        if (ShouldSuppressNativeFadeTransactionHold(
                Volatile.Read(ref _nativeFadeTransactionActive) != 0, nativeUiCursorOwner) ||
            ShouldSuppressNativeFadeCursor(
                Volatile.Read(ref _lastFadeProbeStatus), Volatile.Read(ref _lastFadeMode)))
            return true;
        if (IsWithinQpcGrace(now, Volatile.Read(ref _lastNativeInputDisabledQpc), NativeFadeBoundaryBridgeSeconds) ||
            IsWithinQpcGrace(now, Volatile.Read(ref _lastNativeFadeActiveQpc), NativeFadeBoundaryBridgeSeconds))
            return true;
        return Volatile.Read(ref _lastOperatorKeyState) == 3 ||
               Volatile.Read(ref _splineInputEnabledSinceResume) == 0;
    }

    private bool ShouldSuppressFreeFadeCursor(long now, bool nativeUiCursorOwner)
    {
        long recentFree = Volatile.Read(ref _lastFreeOperationQpc);
        return ShouldSuppressFreeFadeCursorState(
            recentFree != 0 && now >= recentFree && now - recentFree <= Stopwatch.Frequency / 4,
            Volatile.Read(ref _lastFreeDeltaTime),
            Volatile.Read(ref _lastFadeProbeStatus),
            Volatile.Read(ref _lastFadeMode),
            ShouldSuppressNativeFadeTransactionHold(
                Volatile.Read(ref _nativeFadeTransactionActive) != 0, nativeUiCursorOwner),
            IsWithinQpcGrace(now, Volatile.Read(ref _lastFreeNativeInputDisabledQpc), NativeFadeBoundaryBridgeSeconds),
            IsWithinQpcGrace(now, Volatile.Read(ref _lastNativeFadeActiveQpc), NativeFadeBoundaryBridgeSeconds));
    }

    private static bool ShouldSuppressFreeFadeCursorState(
        bool recentFreeCamera, float deltaTime, int fadeProbeStatus, int fadeMode,
        bool nativeFadeTransactionActive, bool withinDisableBridge, bool withinPostFadeBridge) =>
        recentFreeCamera && deltaTime > 0.000001f && fadeProbeStatus == 2 &&
        (nativeFadeTransactionActive || ShouldSuppressNativeFadeCursor(fadeProbeStatus, fadeMode) ||
         withinDisableBridge || withinPostFadeBridge);

    private static bool ShouldSuppressNativeFadeCursor(int fadeProbeStatus, int fadeMode) =>
        fadeProbeStatus == 2 && fadeMode != 0;

    private static bool ShouldSuppressNativeFadeTransactionHold(
        bool nativeFadeTransactionActive, bool nativeUiCursorOwner) =>
        nativeFadeTransactionActive && !nativeUiCursorOwner;

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

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);
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

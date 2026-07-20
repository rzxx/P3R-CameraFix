using p3rpc.camfix.Template;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Continuous, read-only camera-handoff and cursor telemetry sampled from the
/// shared UFldOperationCamera tick. Unlike the spline/free traces, this keeps
/// recording while a specific camera update is absent.
/// </summary>
internal sealed unsafe class CameraTransitionTrace : IDisposable
{
    private const int FldCameraFreeVtableRva = 0x42901B0;
    private const int FldCameraFixedVtableRva = 0x4292560;
    private const int FldCameraSplineVtableRva = 0x42932E8;
    private const int FldCameraHitBoxVtableRva = 0x42939A8;
    private const int FldCameraHitSplineVtableRva = 0x4294058;

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly nint _imageBase;
    private readonly TransitionSlot[] _slots;
    private readonly StreamWriter _writer;
    private readonly Timer _flushTimer;
    private readonly object _writerLock = new();
    private int _reserved;
    private int _read;
    private int _dropped;
    private bool _disposed;

    public CameraTransitionTrace(ModContext context, nint imageBase)
    {
        _logger = context.Logger;
        _imageBase = imageBase;
        int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
        _slots = new TransitionSlot[capacity];

        string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
        string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, $"camera-transition-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
        _writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        _writer.WriteLine($"# process_start_utc={Process.GetCurrentProcess().StartTime.ToUniversalTime():O}");
        _writer.WriteLine($"# image_base=0x{imageBase:X}");
        _writer.Write("sequence,operation_sequence,qpc_enter,qpc_exit,os_thread,operation,delta_time,owner_before,owner_after,operator_key_before,operator_key_after,operator_state_before,operator_state_after,operator_next_before,operator_next_after,camera_lock_before,camera_lock_after,current_hit_before,current_hit_after,hit_vtable_before,hit_vtable_after,camera_before,camera_after,camera_vtable_before,camera_vtable_after,camera_kind_before,camera_kind_after,free_behavior_before,free_behavior_after,free_behavior_vtable_before,free_behavior_vtable_after,camera_input_x_before,camera_input_y_before,camera_input_x_after,camera_input_y_after,hit_yaw_before,hit_pitch_before,hit_yaw_after,hit_pitch_after,margin_yaw_before,margin_pitch_before,margin_yaw_after,margin_pitch_after,kernel_input,default_input_component,current_input_component,show_mouse_cursor,device,device_switch,mouse_source,mouse_x,mouse_y,raw_first_age_ms,raw_last_age_ms,cursor_visible_before,cursor_visible_after,cursor_x_before,cursor_y_before,cursor_x_after,cursor_y_after,cursor_handle_before,cursor_handle_after,wm_setcursor_previous,setcursor_previous,setcursor_zero_previous,setcursor_nonzero_previous,setcursor_suppressed_previous,setcursor_pending_after,setcursor_zero_pending_after,setcursor_nonzero_pending_after,setcursor_suppressed_pending_after,last_setcursor_requested,last_setcursor_applied,last_setcursor_qpc,last_setcursor_age_ms,showcursor_previous,showcursor_pending_after,last_showcursor_show,last_showcursor_result,last_showcursor_qpc,last_showcursor_age_ms,message_window,foreground_window");
        _writer.Write(",fade_probe_status,ui_subsystem,fade_player,fade_programs,fade_program_count,fade_program_max");
        for (int offset = 0x28; offset <= 0x84; offset += 4)
            _writer.Write($",fade_{offset:X2}");
        _writer.Write(",message_probe_status,message_manager_candidates,active_message_managers,active_message_items,active_selection_items,message_manager,message_proc_list,message_proc_count,message_proc_max,message_release_list,message_release_count,message_release_max,message_first_proc,message_first_item,message_first_select");
        _writer.Write(",town_map_probe_status,field_manager_candidates,active_large_map_managers,field_manager,field_operator,large_map_actor,large_map_actor_class_matches,town_map_actor_candidates,live_town_map_actors,town_map_actor,town_map_actor_flags_58,town_map_actor_flags_5c,town_map_actor_input,town_map_actor_root,town_map_location_select,town_map_field_camera,town_map_main_camera,town_map_start_camera");
        _writer.Write(",ui_contact_probe_status,ui_contact_manager_candidates,ui_contact_manager,ui_contact_actor_list,ui_contact_actor_count,ui_contact_actor_max,ui_contact_nonnull_actors,ui_contact_hidden_actors,ui_contact_destroying_actors,ui_contact_input_actors,ui_contact_root_actors,ui_contact_actor_hash");
        for (int index = 0; index < 8; index++)
            _writer.Write($",ui_contact_actor_{index},ui_contact_actor_{index}_class,ui_contact_actor_{index}_input,ui_contact_actor_{index}_root,ui_contact_actor_{index}_flags_58,ui_contact_actor_{index}_flags_5c");
        for (int offset = 0x48; offset <= 0x1D4; offset += 4)
            _writer.Write($",ui_contact_{offset:X3}");
        _writer.Write(",battle_gui_probe_status,battle_gui_manager_candidates,active_battle_gui_managers,battle_gui_manager,battle_gui_now_state,battle_gui_previous_state,battle_gui_state_list,battle_gui_state_count,battle_gui_state_max,battle_gui_flags_58,battle_gui_flags_5c");
        _writer.Write(",clip_query_ok,clip_left,clip_top,clip_right,clip_bottom,capture_window");
        _writer.WriteLine();
        _writer.Flush();
        _flushTimer = new Timer(_ => Flush(), null, 500, 500);
        _logger.WriteLine($"[P3R CamFix] Continuous camera-transition trace active: {tracePath}");
    }

    public CameraTransitionIdentity CaptureIdentity(nint operation)
    {
        nint owner = ReadPointer(operation, 0xB0);
        nint hit = ReadPointer(operation, 0xB8);
        nint hitVtable = ReadPointer(hit, 0);
        nint camera = ResolveOperationCamera(owner, hit, hitVtable);
        nint cameraVtable = ReadPointer(camera, 0);
        CameraKind cameraKind = ClassifyCamera(cameraVtable);
        nint freeBehavior = cameraKind == CameraKind.Free ? ReadPointer(camera, 0x270) : 0;
        nint freeBehaviorVtable = ReadPointer(freeBehavior, 0);

        float hitYaw = float.NaN;
        float hitPitch = float.NaN;
        float marginYaw = float.NaN;
        float marginPitch = float.NaN;
        if (hitVtable == _imageBase + FldCameraHitSplineVtableRva ||
            hitVtable == _imageBase + FldCameraHitBoxVtableRva)
        {
            hitYaw = ReadFloat(hit, 0x2CC);
            hitPitch = ReadFloat(hit, 0x2D0);
            if (hitVtable == _imageBase + FldCameraHitSplineVtableRva)
            {
                marginYaw = ReadFloat(hit, 0x368);
                marginPitch = ReadFloat(hit, 0x36C);
            }
        }

        return new CameraTransitionIdentity
        {
            Owner = owner,
            OperatorKey = owner == 0 ? 0 : *(int*)(owner + 0x280),
            OperatorState = owner == 0 ? 0 : *(int*)(owner + 0x284),
            OperatorNext = owner == 0 ? 0 : *(int*)(owner + 0x288),
            CameraLock = operation == 0 ? 0 : *(byte*)(operation + 0xC0),
            CurrentHit = hit,
            HitVtable = hitVtable,
            Camera = camera,
            CameraVtable = cameraVtable,
            CameraKind = (int)cameraKind,
            FreeBehavior = freeBehavior,
            FreeBehaviorVtable = freeBehaviorVtable,
            CameraInputX = ReadFloat(camera, 0x25C),
            CameraInputY = ReadFloat(camera, 0x260),
            HitYaw = hitYaw,
            HitPitch = hitPitch,
            MarginYaw = marginYaw,
            MarginPitch = marginPitch,
        };
    }

    public void Capture(in CameraTransitionObservation observation, in FadeSnapshot fade)
    {
        CameraTransitionIdentity before = observation.IdentityBefore;
        CameraTransitionIdentity after = CaptureIdentity(observation.Operation);
        Native.Rect clip = default;
        int clipQueryOk = Native.GetClipCursor(&clip);

        var slot = new TransitionSlot
        {
            OperationSequence = observation.OperationSequence,
            QpcEnter = observation.QpcEnter,
            QpcExit = observation.QpcExit,
            OsThread = unchecked((int)Native.GetCurrentThreadId()),
            Operation = observation.Operation,
            DeltaTime = observation.DeltaTime,
            IdentityBefore = before,
            IdentityAfter = after,
            KernelInput = observation.KernelInput,
            DefaultInputComponent = observation.DefaultInputComponent,
            CurrentInputComponent = observation.CurrentInputComponent,
            ShowMouseCursor = observation.ShowMouseCursor,
            Device = observation.Device,
            DeviceSwitch = observation.DeviceSwitch,
            MouseSource = observation.MouseSource,
            MouseX = observation.MouseX,
            MouseY = observation.MouseY,
            RawFirstAgeMs = QpcAgeMilliseconds(observation.QpcEnter, observation.RawFirstQpc),
            RawLastAgeMs = QpcAgeMilliseconds(observation.QpcEnter, observation.RawLastQpc),
            CursorVisibleBefore = observation.CursorVisibleBefore,
            CursorVisibleAfter = observation.CursorVisibleAfter,
            CursorXBefore = observation.CursorXBefore,
            CursorYBefore = observation.CursorYBefore,
            CursorXAfter = observation.CursorXAfter,
            CursorYAfter = observation.CursorYAfter,
            CursorHandleBefore = observation.CursorHandleBefore,
            CursorHandleAfter = observation.CursorHandleAfter,
            WmSetCursorPrevious = observation.WmSetCursorPrevious,
            SetCursorPrevious = observation.SetCursorPrevious,
            SetCursorZeroPrevious = observation.SetCursorZeroPrevious,
            SetCursorNonzeroPrevious = observation.SetCursorNonzeroPrevious,
            SetCursorSuppressedPrevious = observation.SetCursorSuppressedPrevious,
            SetCursorPendingAfter = observation.SetCursorPendingAfter,
            SetCursorZeroPendingAfter = observation.SetCursorZeroPendingAfter,
            SetCursorNonzeroPendingAfter = observation.SetCursorNonzeroPendingAfter,
            SetCursorSuppressedPendingAfter = observation.SetCursorSuppressedPendingAfter,
            LastSetCursorRequested = observation.LastSetCursorRequested,
            LastSetCursorApplied = observation.LastSetCursorApplied,
            LastSetCursorQpc = observation.LastSetCursorQpc,
            LastSetCursorAgeMs = QpcAgeMilliseconds(observation.QpcExit, observation.LastSetCursorQpc),
            ShowCursorPrevious = observation.ShowCursorPrevious,
            ShowCursorPendingAfter = observation.ShowCursorPendingAfter,
            LastShowCursorShow = observation.LastShowCursorShow,
            LastShowCursorResult = observation.LastShowCursorResult,
            LastShowCursorQpc = observation.LastShowCursorQpc,
            LastShowCursorAgeMs = QpcAgeMilliseconds(observation.QpcExit, observation.LastShowCursorQpc),
            MessageWindow = observation.MessageWindow,
            ForegroundWindow = Native.GetForegroundWindow(),
            Fade = fade,
            ClipQueryOk = clipQueryOk,
            ClipLeft = clip.Left,
            ClipTop = clip.Top,
            ClipRight = clip.Right,
            ClipBottom = clip.Bottom,
            CaptureWindow = Native.GetCapture(),
        };
        Reserve(slot);
    }

    private nint ResolveOperationCamera(nint owner, nint hit, nint hitVtable)
    {
        if (hit == 0)
            return ReadPointer(owner, 0x240);
        if (hitVtable == _imageBase + FldCameraHitSplineVtableRva)
            return ReadSplineCamera(hit, 0);
        if (hitVtable == _imageBase + FldCameraHitBoxVtableRva)
            return ReadPointer(hit, 0x2E8);
        return 0;
    }

    private CameraKind ClassifyCamera(nint vtable)
    {
        if (vtable == 0) return CameraKind.None;
        if (vtable == _imageBase + FldCameraFreeVtableRva) return CameraKind.Free;
        if (vtable == _imageBase + FldCameraSplineVtableRva) return CameraKind.Spline;
        if (vtable == _imageBase + FldCameraFixedVtableRva) return CameraKind.Fixed;
        return CameraKind.Unknown;
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

    private static nint ReadPointer(nint address, int offset) => address == 0 ? 0 : *(nint*)(address + offset);
    private static float ReadFloat(nint address, int offset) => address == 0 ? float.NaN : *(float*)(address + offset);

    private static float QpcAgeMilliseconds(long now, long timestamp)
    {
        if (timestamp == 0 || now < timestamp) return float.NaN;
        return (float)((now - timestamp) * 1000.0 / Stopwatch.Frequency);
    }

    private void Reserve(TransitionSlot value)
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

    private void Flush()
    {
        if (_disposed) return;
        lock (_writerLock)
        {
            if (!_disposed) DrainLocked();
        }
    }

    private void DrainLocked()
    {
        int limit = Math.Min(Volatile.Read(ref _reserved), _slots.Length);
        while (_read < limit)
        {
            ref TransitionSlot slot = ref _slots[_read];
            if (Volatile.Read(ref slot.Ready) == 0) break;
            WriteSlot(slot);
            _read++;
        }
        _writer.Flush();
    }

    private void WriteSlot(in TransitionSlot s)
    {
        _writer.Write(s.Sequence.ToString(CultureInfo.InvariantCulture));
        WriteInt(s.OperationSequence);
        WriteLong(s.QpcEnter, s.QpcExit);
        WriteInt(s.OsThread);
        WritePointer(s.Operation);
        WriteFloat(s.DeltaTime);
        CameraTransitionIdentity b = s.IdentityBefore;
        CameraTransitionIdentity a = s.IdentityAfter;
        WritePointer(b.Owner); WritePointer(a.Owner);
        WriteInt(b.OperatorKey, a.OperatorKey, b.OperatorState, a.OperatorState,
            b.OperatorNext, a.OperatorNext, b.CameraLock, a.CameraLock);
        WritePointer(b.CurrentHit); WritePointer(a.CurrentHit);
        WritePointer(b.HitVtable); WritePointer(a.HitVtable);
        WritePointer(b.Camera); WritePointer(a.Camera);
        WritePointer(b.CameraVtable); WritePointer(a.CameraVtable);
        _writer.Write(','); _writer.Write(((CameraKind)b.CameraKind).ToString().ToLowerInvariant());
        _writer.Write(','); _writer.Write(((CameraKind)a.CameraKind).ToString().ToLowerInvariant());
        WritePointer(b.FreeBehavior); WritePointer(a.FreeBehavior);
        WritePointer(b.FreeBehaviorVtable); WritePointer(a.FreeBehaviorVtable);
        WriteFloat(b.CameraInputX); WriteFloat(b.CameraInputY); WriteFloat(a.CameraInputX); WriteFloat(a.CameraInputY);
        WriteFloat(b.HitYaw); WriteFloat(b.HitPitch); WriteFloat(a.HitYaw); WriteFloat(a.HitPitch);
        WriteFloat(b.MarginYaw); WriteFloat(b.MarginPitch); WriteFloat(a.MarginYaw); WriteFloat(a.MarginPitch);
        WritePointer(s.KernelInput); WritePointer(s.DefaultInputComponent); WritePointer(s.CurrentInputComponent);
        WriteInt(s.ShowMouseCursor, s.Device, s.DeviceSwitch, s.MouseSource, s.MouseX, s.MouseY);
        WriteFloat(s.RawFirstAgeMs); WriteFloat(s.RawLastAgeMs);
        WriteInt(s.CursorVisibleBefore, s.CursorVisibleAfter, s.CursorXBefore, s.CursorYBefore,
            s.CursorXAfter, s.CursorYAfter);
        WritePointer(s.CursorHandleBefore); WritePointer(s.CursorHandleAfter);
        WriteInt(s.WmSetCursorPrevious, s.SetCursorPrevious, s.SetCursorZeroPrevious,
            s.SetCursorNonzeroPrevious, s.SetCursorSuppressedPrevious, s.SetCursorPendingAfter,
            s.SetCursorZeroPendingAfter, s.SetCursorNonzeroPendingAfter, s.SetCursorSuppressedPendingAfter);
        WritePointer(s.LastSetCursorRequested); WritePointer(s.LastSetCursorApplied); WriteLong(s.LastSetCursorQpc);
        WriteFloat(s.LastSetCursorAgeMs);
        WriteInt(s.ShowCursorPrevious, s.ShowCursorPendingAfter, s.LastShowCursorShow, s.LastShowCursorResult);
        WriteLong(s.LastShowCursorQpc); WriteFloat(s.LastShowCursorAgeMs);
        WritePointer(s.MessageWindow); WritePointer(s.ForegroundWindow);
        FadeSnapshot fade = s.Fade;
        WriteInt(fade.Status);
        WritePointer(fade.UiSubsystem); WritePointer(fade.FadePlayer); WritePointer(fade.Programs);
        WriteInt(fade.ProgramCount, fade.ProgramMax);
        for (int index = 0; index < FadeSnapshot.WordCount; index++)
            WriteUInt(fade.GetWord(index));
        WriteInt(fade.MessageProbeStatus, fade.MessageManagerCandidateCount, fade.ActiveMessageManagerCount,
            fade.ActiveMessageItemCount, fade.ActiveSelectionItemCount);
        WritePointer(fade.MessageManager); WritePointer(fade.MessageProcList);
        WriteInt(fade.MessageProcCount, fade.MessageProcMax);
        WritePointer(fade.MessageReleaseList);
        WriteInt(fade.MessageReleaseCount, fade.MessageReleaseMax);
        WritePointer(fade.MessageFirstProc); WritePointer(fade.MessageFirstItem); WritePointer(fade.MessageFirstSelect);
        WriteInt(fade.TownMapProbeStatus, fade.FieldManagerCandidateCount, fade.ActiveLargeMapManagerCount);
        WritePointer(fade.FieldManager); WritePointer(fade.FieldOperator); WritePointer(fade.LargeMapActor);
        WriteInt(fade.LargeMapActorClassMatches, fade.TownMapActorCandidateCount, fade.LiveTownMapActorCount);
        WritePointer(fade.TownMapActor);
        WriteUInt(fade.TownMapActorFlags58); WriteUInt(fade.TownMapActorFlags5C);
        WritePointer(fade.TownMapActorInput); WritePointer(fade.TownMapActorRoot);
        WritePointer(fade.TownMapLocationSelect); WritePointer(fade.TownMapFieldCamera);
        WritePointer(fade.TownMapMainCamera); WritePointer(fade.TownMapStartCamera);
        WriteInt(fade.UiContactProbeStatus, fade.UiContactManagerCandidateCount);
        WritePointer(fade.UiContactManager); WritePointer(fade.UiContactActorList);
        WriteInt(fade.UiContactActorCount, fade.UiContactActorMax, fade.UiContactNonNullActorCount,
            fade.UiContactHiddenActorCount, fade.UiContactDestroyingActorCount,
            fade.UiContactInputActorCount, fade.UiContactRootActorCount);
        WriteULong(fade.UiContactActorHash);
        for (int index = 0; index < 8; index++)
        {
            WritePointer(fade.GetUiContactActor(index));
            WritePointer(fade.GetUiContactActorClass(index));
            WritePointer(fade.GetUiContactActorInput(index));
            WritePointer(fade.GetUiContactActorRoot(index));
            WriteUInt(fade.GetUiContactActorFlags58(index));
            WriteUInt(fade.GetUiContactActorFlags5C(index));
        }
        for (int index = 0; index < 100; index++)
            WriteUInt(fade.GetUiContactWord(index));
        WriteInt(fade.BattleGuiProbeStatus, fade.BattleGuiManagerCandidateCount,
            fade.ActiveBattleGuiManagerCount);
        WritePointer(fade.BattleGuiManager);
        WriteInt(fade.BattleGuiNowState, fade.BattleGuiPreviousState);
        WritePointer(fade.BattleGuiStateList);
        WriteInt(fade.BattleGuiStateCount, fade.BattleGuiStateMax);
        WriteUInt(fade.BattleGuiFlags58); WriteUInt(fade.BattleGuiFlags5C);
        WriteInt(s.ClipQueryOk, s.ClipLeft, s.ClipTop, s.ClipRight, s.ClipBottom);
        WritePointer(s.CaptureWindow);
        _writer.WriteLine();
    }

    private void WriteInt(params int[] values)
    {
        foreach (int value in values) { _writer.Write(','); _writer.Write(value.ToString(CultureInfo.InvariantCulture)); }
    }

    private void WriteLong(params long[] values)
    {
        foreach (long value in values) { _writer.Write(','); _writer.Write(value.ToString(CultureInfo.InvariantCulture)); }
    }

    private void WriteUInt(uint value)
    {
        _writer.Write(','); _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteULong(ulong value)
    {
        _writer.Write(','); _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    private void WritePointer(nint value)
    {
        _writer.Write(",0x"); _writer.Write(value.ToString("X", CultureInfo.InvariantCulture));
    }

    private void WriteFloat(float value)
    {
        _writer.Write(','); _writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_writerLock)
        {
            if (!_disposed)
            {
                DrainLocked();
                _writer.WriteLine($"# captured={_read.ToString(CultureInfo.InvariantCulture)}");
                _writer.WriteLine($"# dropped={Volatile.Read(ref _dropped).ToString(CultureInfo.InvariantCulture)}");
                _writer.Dispose();
                _disposed = true;
            }
        }
        _flushTimer.Dispose();
    }

    private enum CameraKind { None, Free, Spline, Fixed, Unknown }

    private struct TransitionSlot
    {
        public int Ready, Sequence, OperationSequence, OsThread;
        public long QpcEnter, QpcExit, LastSetCursorQpc, LastShowCursorQpc;
        public nint Operation;
        public CameraTransitionIdentity IdentityBefore, IdentityAfter;
        public nint KernelInput, DefaultInputComponent, CurrentInputComponent;
        public nint CursorHandleBefore, CursorHandleAfter, LastSetCursorRequested, LastSetCursorApplied;
        public nint MessageWindow, ForegroundWindow;
        public nint CaptureWindow;
        public int ShowMouseCursor, Device, DeviceSwitch, MouseSource, MouseX, MouseY;
        public int ClipQueryOk, ClipLeft, ClipTop, ClipRight, ClipBottom;
        public int CursorVisibleBefore, CursorVisibleAfter, CursorXBefore, CursorYBefore, CursorXAfter, CursorYAfter;
        public int WmSetCursorPrevious, SetCursorPrevious, SetCursorZeroPrevious, SetCursorNonzeroPrevious, SetCursorSuppressedPrevious;
        public int SetCursorPendingAfter, SetCursorZeroPendingAfter, SetCursorNonzeroPendingAfter, SetCursorSuppressedPendingAfter;
        public int ShowCursorPrevious, ShowCursorPendingAfter, LastShowCursorShow, LastShowCursorResult;
        public float DeltaTime;
        public float RawFirstAgeMs, RawLastAgeMs, LastSetCursorAgeMs, LastShowCursorAgeMs;
        public FadeSnapshot Fade;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetClipCursor(Rect* rect);

        [DllImport("user32.dll")]
        public static extern nint GetCapture();
    }
}

internal struct CameraTransitionObservation
{
    public int OperationSequence;
    public long QpcEnter, QpcExit;
    public nint Operation;
    public float DeltaTime;
    public CameraTransitionIdentity IdentityBefore;
    public nint KernelInput, DefaultInputComponent, CurrentInputComponent;
    public int ShowMouseCursor, Device, DeviceSwitch, MouseSource, MouseX, MouseY;
    public long RawFirstQpc, RawLastQpc;
    public int CursorVisibleBefore, CursorVisibleAfter, CursorXBefore, CursorYBefore, CursorXAfter, CursorYAfter;
    public nint CursorHandleBefore, CursorHandleAfter;
    public int WmSetCursorPrevious, SetCursorPrevious, SetCursorZeroPrevious, SetCursorNonzeroPrevious, SetCursorSuppressedPrevious;
    public int SetCursorPendingAfter, SetCursorZeroPendingAfter, SetCursorNonzeroPendingAfter, SetCursorSuppressedPendingAfter;
    public nint LastSetCursorRequested, LastSetCursorApplied;
    public long LastSetCursorQpc;
    public int ShowCursorPrevious, ShowCursorPendingAfter, LastShowCursorShow, LastShowCursorResult;
    public long LastShowCursorQpc;
    public nint MessageWindow;
}

internal struct CameraTransitionIdentity
{
    public nint Owner, CurrentHit, HitVtable, Camera, CameraVtable, FreeBehavior, FreeBehaviorVtable;
    public int OperatorKey, OperatorState, OperatorNext, CameraLock, CameraKind;
    public float CameraInputX, CameraInputY, HitYaw, HitPitch, MarginYaw, MarginPitch;
}

using p3rpc.camfix.Template;
using System.Diagnostics;
using System.Globalization;

namespace p3rpc.camfix;

/// <summary>
/// Tiny, immediately-flushed index for user-observed trace events. Marker QPC
/// values share the same Stopwatch clock as every camera trace, so one press
/// locates the corresponding rows without adding a marker column to each file.
/// </summary>
internal sealed class TraceMarkerRecorder : IDisposable
{
    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly StreamWriter _writer;
    private readonly object _writerLock = new();
    private int _sequence;
    private bool _disposed;

    public TraceMarkerRecorder(ModContext context)
    {
        _logger = context.Logger;
        string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
        string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, $"trace-markers-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(new FileStream(
            tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.SequentialScan));
        _writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        _writer.WriteLine($"# process_start_utc={Process.GetCurrentProcess().StartTime.ToUniversalTime():O}");
        _writer.WriteLine("marker,qpc,utc,operation_sequence,operator_key_state,operator_state,operator_next_state,camera_lock,device,cursor_visible,cursor_handle,last_setcursor_requested,last_setcursor_applied,last_setcursor_qpc,fade_probe_status,fade_mode,fade_transaction_active");
        _writer.Flush();
        _logger.WriteLine($"[P3R CamFix] Trace marker active: press Page Up after a problem ({tracePath}).");
    }

    public void Capture(in TraceMarkerSnapshot snapshot)
    {
        lock (_writerLock)
        {
            if (_disposed)
                return;

            int marker = ++_sequence;
            _writer.Write(marker.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.Qpc.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.OperationSequence.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.OperatorKeyState.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.OperatorState.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.OperatorNextState.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.CameraLock.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.Device.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.CursorVisible.ToString(CultureInfo.InvariantCulture));
            WritePointer(snapshot.CursorHandle);
            WritePointer(snapshot.LastSetCursorRequested);
            WritePointer(snapshot.LastSetCursorApplied);
            _writer.Write(','); _writer.Write(snapshot.LastSetCursorQpc.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.FadeProbeStatus.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.FadeMode.ToString(CultureInfo.InvariantCulture));
            _writer.Write(','); _writer.Write(snapshot.FadeTransactionActive.ToString(CultureInfo.InvariantCulture));
            _writer.WriteLine();
            _writer.Flush();
            _logger.WriteLine($"[P3R CamFix] Trace marker {marker} recorded (Page Up, QPC {snapshot.Qpc}).");
        }
    }

    private void WritePointer(nint value)
    {
        _writer.Write(",0x");
        _writer.Write(value.ToString("X", CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        lock (_writerLock)
        {
            if (_disposed)
                return;
            _writer.Dispose();
            _disposed = true;
        }
    }
}

internal readonly record struct TraceMarkerSnapshot(
    long Qpc,
    int OperationSequence,
    int OperatorKeyState,
    int OperatorState,
    int OperatorNextState,
    int CameraLock,
    int Device,
    int CursorVisible,
    nint CursorHandle,
    nint LastSetCursorRequested,
    nint LastSetCursorApplied,
    long LastSetCursorQpc,
    int FadeProbeStatus,
    int FadeMode,
    int FadeTransactionActive);

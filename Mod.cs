using p3rpc.camfix.Configuration;
using p3rpc.camfix.Template;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

public class Mod : ModBase
{
    private static ILogger? _logger;
    private readonly IModConfig _modConfig;
    public static Config Configuration = null!;

    private static unsafe UnrealTypes.FUObjectArray* _gUObjectArray;
    private static unsafe UnrealTypes.FNamePool* _gNamePool;

    private static readonly object _lock = new();
    private static readonly List<IntPtr> _cachedBehaviors = new();

    // Cached class FName PoolLocations for int-based class matching.
    // 0 = not yet resolved. Resolved on first successful scan, then all
    // subsequent scans/liveness checks use integer comparison instead of
    // allocating managed strings via Marshal.PtrToString*.
    private static uint _fldCameraBehaviorFreeNameLoc;
    private static uint _bpFldCameraBehaviorFreeCNameLoc;

    private static Timer? _timer;
    private static bool _sigScanDone;
    private static bool _valuesApplied;
    private static int _scanAttempts;

    // Phase 1 (scan):      fires every 5s until behaviors are found.
    // Phase 2 (liveness):  fires every 15s with a cheap int-compare check.
    // The liveness check costs ~10ns per cached behavior and allocates nothing.
    private const int ScanIntervalMs = 5000;
    private const int LivenessIntervalMs = 15000;

    public Mod(ModContext context)
    {
        _logger = context.Logger;
        _modConfig = context.ModConfig;
        Configuration = context.Configuration;

        var baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;
        Log($"Initializing. Base address: 0x{baseAddress:X}");

        if (context.StartupScanner == null)
        {
            LogError("No startup scanner available. Mod will not work.");
            return;
        }

        ScanForGlobals(context.StartupScanner, baseAddress);
    }

    private unsafe void ScanForGlobals(IStartupScanner scanner, nint baseAddress)
    {
        // FUObjectArray sig (from p3rpc.nativetypes)
        const string fuObjectArraySig = "48 8B 05 ?? ?? ?? ?? 48 8B 0C ?? 48 8D 04 ?? 48 85 C0 74 ?? 44 39 40 ?? 75 ?? F7 40 ?? 00 00 00 30 75 ?? 48 8B 00";

        // FGlobalNamePool sig (from p3rpc.nativetypes)
        const string fNamePoolSig = "4C 8D 05 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 C6 05 ?? ?? ?? ?? 01 48 8B 44 24 ?? 48 8B D3 48 C1 E8 20 8D 0C ?? 49 03 4C ?? ?? E8 ?? ?? ?? ?? 48 8B C3";

        scanner.AddMainModuleScan(fuObjectArraySig, result =>
        {
            if (!result.Found)
            {
                LogError("FUObjectArray sig not found!");
                return;
            }
            // The sig finds `mov rax, [rip+X]` which loads the Objects field (at offset 0x10)
            // from within the GUObjectArray global struct. So the resolved address points to
            // &GUObjectArray.Objects, meaning the FUObjectArray struct starts 0x10 bytes before.
            // No dereference needed — GUObjectArray is a static global, not a pointer.
            var instrAddr = baseAddress + result.Offset;
            var resolvedAddr = ResolveRipRelative(instrAddr);
            _gUObjectArray = (UnrealTypes.FUObjectArray*)(resolvedAddr - 0x10);
            Log($"Found FUObjectArray.Objects field at 0x{resolvedAddr:X}, struct at 0x{(nint)_gUObjectArray:X}");
            TryStartTimer();
        });

        scanner.AddMainModuleScan(fNamePoolSig, result =>
        {
            if (!result.Found)
            {
                LogError("FNamePool sig not found!");
                return;
            }
            // FNamePool sig uses `lea r8, [rip+X]` which gives the address of the global directly.
            var instrAddr = baseAddress + result.Offset;
            _gNamePool = (UnrealTypes.FNamePool*)ResolveRipRelative(instrAddr);
            Log($"Found FNamePool at 0x{(nint)_gNamePool:X}");
            TryStartTimer();
        });
    }

    private static unsafe nint ResolveRipRelative(nint instrAddr)
    {
        // For `mov rax, [rip+disp32]` or `lea rax, [rip+disp32]`:
        // The displacement is a 32-bit signed int at offset +3 (after 48 8B 05 / 48 8D 05)
        // But the sig might match at different instruction encodings.
        // We scan the first few bytes for the pattern and find the disp32.
        // Common: 48 8B 05 XX XX XX XX (7 bytes, disp at +3)
        //         4C 8D 05 XX XX XX XX (7 bytes, disp at +3)
        int disp;
        byte b0 = *(byte*)instrAddr;
        byte b1 = *(byte*)(instrAddr + 1);
        byte b2 = *(byte*)(instrAddr + 2);

        if ((b0 == 0x48 || b0 == 0x4C) && (b1 == 0x8B || b1 == 0x8D) && b2 == 0x05)
        {
            disp = *(int*)(instrAddr + 3);
            return instrAddr + 7 + disp;
        }

        // Fallback: try 48 8D 0D (lea rcx)
        if (b0 == 0x48 && b1 == 0x8D && b2 == 0x0D)
        {
            disp = *(int*)(instrAddr + 3);
            return instrAddr + 7 + disp;
        }

        // Another fallback: just try offset +3
        disp = *(int*)(instrAddr + 3);
        return instrAddr + 7 + disp;
    }

    private static unsafe void TryStartTimer()
    {
        if (_sigScanDone) return;
        if (_gUObjectArray != null && _gNamePool != null)
        {
            _sigScanDone = true;
            Log("Both globals found. Starting camera fix timer (scan phase).");
            _timer = new Timer(_ => Tick(), null, ScanIntervalMs, ScanIntervalMs);
        }
    }

    private static unsafe void Tick()
    {
        if (!Configuration.Enabled) return;
        if (_gUObjectArray == null || _gNamePool == null) return;

        try
        {
            lock (_lock)
            {
                // Liveness phase: we have cached behaviors, check them cheaply.
                if (_cachedBehaviors.Count > 0)
                {
                    if (AreBehaviorsValid())
                    {
                        // Still valid. Re-apply values only if config changed or
                        // the game reset them (rare).
                        if (!_valuesApplied)
                        {
                            ApplyValuesToAllBehaviors();
                            _valuesApplied = true;
                            Log("Re-applied values after config change.");
                        }
                        EnsureTimerInterval(LivenessIntervalMs);
                        return;
                    }

                    // A cached pointer went stale (map changed, behavior destroyed).
                    // Drop the cache and fall through to the scan phase.
                    Log("Cached behaviors are stale. Rescanning...");
                    _cachedBehaviors.Clear();
                    _valuesApplied = false;
                }

                // Scan phase: walk the UObject array looking for camera behaviors.
                _scanAttempts++;
                ScanForBehaviors();

                if (_cachedBehaviors.Count > 0)
                {
                    ApplyValuesToAllBehaviors();
                    _valuesApplied = true;
                    EnsureTimerInterval(LivenessIntervalMs);
                }
                else
                {
                    // Stay in scan phase. Throttle the "nothing found" log so we
                    // don't spam while the player sits in a menu / battle.
                    if (_scanAttempts % 12 == 0)
                        Log($"Scan attempt {_scanAttempts}: no field camera behaviors found yet.");
                    EnsureTimerInterval(ScanIntervalMs);
                }
            }
        }
        catch (Exception e)
        {
            LogError($"Tick error: {e.Message}");
        }
    }

    private static void EnsureTimerInterval(int intervalMs)
    {
        // Adapt the timer to the current phase. Timer.Change is cheap and
        // idempotent if the interval is already correct.
        _timer?.Change(intervalMs, intervalMs);
    }

    /// <summary>
    /// Walks the entire FUObjectArray looking for live (non-CDO)
    /// FldCameraBehaviorFree / BP_FldCameraBehaviorFree_C instances.
    ///
    /// The first scan uses string comparison to resolve the class FName
    /// PoolLocations. All subsequent scans use integer comparison on the
    /// cached PoolLocations — no managed string allocations.
    /// </summary>
    private static unsafe void ScanForBehaviors()
    {
        var arr = _gUObjectArray!;
        int count = arr->NumElements;
        int numChunks = arr->NumChunks;
        var objects = arr->Objects;

        if (count <= 0 || numChunks <= 0 || objects == null)
        {
            if (_scanAttempts % 6 == 0)
                Log($"FUObjectArray not initialized yet (NumElements={count}, NumChunks={numChunks}). Will retry...");
            return;
        }

        bool useIntCompare = _fldCameraBehaviorFreeNameLoc != 0;
        int found = 0;
        int scanned = 0;

        for (int chunkIdx = 0; chunkIdx < numChunks; chunkIdx++)
        {
            var chunk = objects[chunkIdx];
            if (chunk == null) continue;
            int chunkSize = Math.Min(0x10000, count - chunkIdx * 0x10000);
            for (int i = 0; i < chunkSize; i++)
            {
                var obj = chunk[i].Object;
                if (obj == null) continue;
                scanned++;

                bool isMatch;
                if (useIntCompare)
                {
                    // Hot path: 1 pointer deref + 1 uint read + 2 uint compares.
                    // No string allocation, no exception handling.
                    isMatch = IsCameraBehaviorClassFast(obj);
                }
                else
                {
                    // First scan only: string compare to resolve FName IDs.
                    // After this, we never allocate a string for class matching again.
                    string? className = GetClassName(obj);
                    if (string.IsNullOrEmpty(className)) continue;
                    isMatch = className == "FldCameraBehaviorFree" || className == "BP_FldCameraBehaviorFree_C";
                    if (isMatch)
                        CacheClassNamePoolLoc(obj, className);
                }

                if (isMatch)
                {
                    bool isCDO = (obj->ObjectFlags & 0x10) != 0;
                    if (!isCDO)
                    {
                        _cachedBehaviors.Add((IntPtr)obj);
                        found++;
                        Log($"Found behavior at 0x{(nint)obj:X} (CDO={isCDO})");
                    }
                }
            }
        }

        if (found > 0)
        {
            Log($"Scan complete. Scanned {scanned} objects, found {found} non-CDO behavior(s).");
            Log($"Switching to liveness phase (every {LivenessIntervalMs / 1000}s).");
        }
        else if (_scanAttempts % 12 == 0)
        {
            Log($"Scan complete. Scanned {scanned} objects, found 0 behaviors. Game may not be in a field map.");
        }
    }

    /// <summary>
    /// Cheap liveness check for cached behaviors.
    /// Does NOT allocate strings. Uses integer comparison on the cached
    /// class FName PoolLocations. Cost: ~10ns per cached behavior.
    /// </summary>
    private static unsafe bool AreBehaviorsValid()
    {
        for (int i = 0; i < _cachedBehaviors.Count; i++)
        {
            var obj = (UnrealTypes.UObject*)_cachedBehaviors[i];
            if (obj == null) return false;
            if ((obj->ObjectFlags & 0x10) != 0) return false; // became a CDO?
            if (obj->ClassPrivate == null) return false;
            var poolLoc = obj->ClassPrivate->baseObj.NamePrivate.PoolLocation;
            if (poolLoc != _fldCameraBehaviorFreeNameLoc && poolLoc != _bpFldCameraBehaviorFreeCNameLoc)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Integer-compare class check for the scan hot path.
    /// No string allocation. No exception handling.
    /// </summary>
    private static unsafe bool IsCameraBehaviorClassFast(UnrealTypes.UObject* obj)
    {
        if (obj->ClassPrivate == null) return false;
        var poolLoc = obj->ClassPrivate->baseObj.NamePrivate.PoolLocation;
        return poolLoc == _fldCameraBehaviorFreeNameLoc || poolLoc == _bpFldCameraBehaviorFreeCNameLoc;
    }

    /// <summary>
    /// Caches the FName PoolLocation for a class so future scans/liveness
    /// checks can use integer comparison instead of string comparison.
    /// Called once per class on the first successful match.
    /// </summary>
    private static unsafe void CacheClassNamePoolLoc(UnrealTypes.UObject* obj, string className)
    {
        if (obj->ClassPrivate == null) return;
        var poolLoc = obj->ClassPrivate->baseObj.NamePrivate.PoolLocation;
        if (className == "FldCameraBehaviorFree" && _fldCameraBehaviorFreeNameLoc == 0)
        {
            _fldCameraBehaviorFreeNameLoc = poolLoc;
            Log($"Cached FName PoolLocation for FldCameraBehaviorFree: 0x{poolLoc:X}");
        }
        else if (className == "BP_FldCameraBehaviorFree_C" && _bpFldCameraBehaviorFreeCNameLoc == 0)
        {
            _bpFldCameraBehaviorFreeCNameLoc = poolLoc;
            Log($"Cached FName PoolLocation for BP_FldCameraBehaviorFree_C: 0x{poolLoc:X}");
        }
    }

    private static unsafe void ApplyValuesToAllBehaviors()
    {
        foreach (var ptr in _cachedBehaviors)
            ApplyValuesToBehavior((UnrealTypes.UObject*)ptr);
    }

    /// <summary>
    /// Reads the class name string from the FName pool. Only used during the
    /// first scan to resolve FName PoolLocations. After that, all class
    /// matching uses integer comparison (IsCameraBehaviorClassFast).
    /// </summary>
    private static unsafe string GetClassName(UnrealTypes.UObject* obj)
    {
        if (obj->ClassPrivate == null) return "";
        return _gNamePool->GetString(obj->ClassPrivate->baseObj.NamePrivate);
    }

    private static unsafe void ApplyValuesToBehavior(UnrealTypes.UObject* behavior)
    {
        // FldCameraRotParam layout: Speed(0), Acceleration(4), Deceleration(8), Press(12), Release(16) = 20 bytes
        // UFldCameraBehaviorFree offsets (from v0.7 dump):
        //   0x00E8: YawParam     (FldCameraRotParam, 20 bytes)
        //   0x0104: PitchParam   (FldCameraRotParam, 20 bytes)
        //   0x0120: CorrectionParam (FldCameraCorrectionParam = FldCameraRotParam + Margin, 24 bytes)

        nint baseAddr = (nint)behavior;

        // Read current values to check if we need to write (avoid unnecessary writes).
        // The game only resets these when the behavior is (re)initialized, not
        // every frame, so this check is almost always false after the first apply.
        float yawAccelCur = *(float*)(baseAddr + 0x00E8 + 4);
        float pitchAccelCur = *(float*)(baseAddr + 0x0104 + 4);
        float correctionAccelCur = *(float*)(baseAddr + 0x0120 + 4);

        bool needsWrite = Math.Abs(yawAccelCur - Configuration.YawAcceleration) > 0.0001f ||
                          Math.Abs(pitchAccelCur - Configuration.PitchAcceleration) > 0.0001f ||
                          Math.Abs(correctionAccelCur - Configuration.CorrectionAcceleration) > 0.0001f;

        if (!needsWrite) return;

        // YawParam at 0x00E8
        WriteRotParam(baseAddr + 0x00E8,
            Configuration.YawSpeed,
            Configuration.YawAcceleration,
            Configuration.YawDeceleration,
            Configuration.YawPress,
            Configuration.YawRelease);

        // PitchParam at 0x0104
        WriteRotParam(baseAddr + 0x0104,
            Configuration.PitchSpeed,
            Configuration.PitchAcceleration,
            Configuration.PitchDeceleration,
            Configuration.PitchPress,
            Configuration.PitchRelease);

        // CorrectionParam at 0x0120
        WriteRotParam(baseAddr + 0x0120,
            Configuration.CorrectionSpeed,
            Configuration.CorrectionAcceleration,
            Configuration.CorrectionDeceleration,
            Configuration.CorrectionPress,
            Configuration.CorrectionRelease);

        Log($"Applied values to behavior at 0x{baseAddr:X}");
    }

    private static unsafe void WriteRotParam(nint addr, float speed, float accel, float decel, float press, float release)
    {
        *(float*)(addr + 0) = speed;
        *(float*)(addr + 4) = accel;
        *(float*)(addr + 8) = decel;
        *(float*)(addr + 12) = press;
        *(float*)(addr + 16) = release;
    }

    private static void Log(string msg) => _logger?.WriteLine($"[P3R CamFix] {msg}");
    private static void LogError(string msg) => _logger?.WriteLine($"[P3R CamFix] {msg}", System.Drawing.Color.Red);

    public override void ConfigurationUpdated(Config configuration)
    {
        Configuration = configuration;
        _logger?.WriteLine($"[{_modConfig.ModId}] Config updated. Will re-apply values on next tick.");
        // Don't clear the cache — the behavior pointers are still valid.
        // Just flip the flag so the next liveness tick re-applies the new values.
        lock (_lock)
        {
            _valuesApplied = false;
        }
    }

    public override void Disposing()
    {
        _timer?.Dispose();
        _timer = null;
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}

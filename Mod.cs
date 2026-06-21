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
    private static unsafe List<IntPtr> _cachedBehaviors = new();
    private static Timer? _applyTimer;
    private static bool _sigScanDone;

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

    private unsafe void TryStartTimer()
    {
        if (_sigScanDone) return;
        if (_gUObjectArray != null && _gNamePool != null)
        {
            _sigScanDone = true;
            Log("Both globals found. Starting camera fix timer.");
            _applyTimer = new Timer(_ => ApplyFixTick(), null, 5000, 1000);
        }
    }

    private static unsafe void ApplyFixTick()
    {
        if (!Configuration.Enabled) return;
        if (_gUObjectArray == null || _gNamePool == null) return;

        try
        {
            // Check if cached behaviors are still valid
            if (_cachedBehaviors.Count > 0 && AreBehaviorsValid())
            {
                foreach (var ptr in _cachedBehaviors)
                    ApplyValuesToBehavior((UnrealTypes.UObject*)ptr);
                return;
            }

            // Cache invalid, find new behaviors
            _cachedBehaviors.Clear();
            var behaviors = FindCameraBehaviors();
            if (behaviors.Count > 0)
            {
                foreach (var b in behaviors)
                {
                    _cachedBehaviors.Add(b);
                    var obj = (UnrealTypes.UObject*)b;
                    Log($"Caching behavior: {GetClassName(obj)} at 0x{(nint)obj:X} Name={GetObjectName(obj)}");
                }
                foreach (var b in behaviors)
                    ApplyValuesToBehavior((UnrealTypes.UObject*)b);
            }
        }
        catch (Exception e)
        {
            LogError($"Tick error: {e.Message}");
        }
    }

    private static unsafe bool AreBehaviorsValid()
    {
        foreach (var ptr in _cachedBehaviors)
        {
            if (!IsBehaviorValid((UnrealTypes.UObject*)ptr))
                return false;
        }
        return true;
    }

    private static unsafe List<IntPtr> FindCameraBehaviors()
    {
        var result = new List<IntPtr>();
        var arr = _gUObjectArray!;
        int count = arr->NumElements;
        int numChunks = arr->NumChunks;
        var objects = arr->Objects;

        if (count <= 0 || numChunks <= 0 || objects == null)
        {
            Log($"FUObjectArray not initialized yet (NumElements={count}, NumChunks={numChunks}, Objects=0x{(nint)objects:X}). Will retry...");
            return result;
        }

        Log($"Scanning {count} objects in {numChunks} chunks for camera behaviors...");

        int scanned = 0;

        for (int chunkIdx = 0; chunkIdx < numChunks; chunkIdx++)
        {
            var chunk = objects[chunkIdx];
            if (chunk == null) continue;
            int chunkSize = Math.Min(0x10000, count - chunkIdx * 0x10000);
            for (int i = 0; i < chunkSize; i++)
            {
                try
                {
                    var obj = chunk[i].Object;
                    if (obj == null) continue;
                    scanned++;
                    var className = GetClassName(obj);
                    if (string.IsNullOrEmpty(className)) continue;

                    if (className == "FldCameraBehaviorFree" || className == "BP_FldCameraBehaviorFree_C")
                    {
                        bool isCDO = (obj->ObjectFlags & 0x10) != 0;
                        string objName = GetObjectName(obj);
                        nint drivedOwner = *(nint*)((nint)obj + 0x00C8);
                        Log($"  MATCH: {className} at 0x{(nint)obj:X} CDO={isCDO} DrivedOwner=0x{drivedOwner:X} Name={objName}");
                        if (!isCDO)
                            result.Add((IntPtr)obj);
                    }
                }
                catch { }
            }
        }

        Log($"Scan complete. Scanned {scanned} objects, found {result.Count} non-CDO behaviors.");
        return result;
    }

    private static unsafe string GetClassName(UnrealTypes.UObject* obj)
    {
        if (obj->ClassPrivate == null) return "";
        var classObj = obj->ClassPrivate;
        return _gNamePool->GetString(classObj->baseObj.NamePrivate);
    }

    private static unsafe string GetObjectName(UnrealTypes.UObject* obj)
    {
        try { return _gNamePool->GetString(obj->NamePrivate); }
        catch { return "?"; }
    }

    private static unsafe bool IsBehaviorValid(UnrealTypes.UObject* obj)
    {
        try
        {
            if (obj == null) return false;
            // Don't use CDOs as the live behavior
            if ((obj->ObjectFlags & 0x10) != 0) return false;
            var name = GetClassName(obj);
            return name == "FldCameraBehaviorFree" || name == "BP_FldCameraBehaviorFree_C";
        }
        catch
        {
            return false;
        }
    }

    private static unsafe void ApplyValuesToBehavior(UnrealTypes.UObject* behavior)
    {
        // FldCameraRotParam layout: Speed(0), Acceleration(4), Deceleration(8), Press(12), Release(16) = 20 bytes
        // UFldCameraBehaviorFree offsets (from v0.7 dump):
        //   0x00E8: YawParam     (FldCameraRotParam, 20 bytes)
        //   0x0104: PitchParam   (FldCameraRotParam, 20 bytes)
        //   0x0120: CorrectionParam (FldCameraCorrectionParam = FldCameraRotParam + Margin, 24 bytes)

        nint baseAddr = (nint)behavior;

        // Read current values to check if we need to write (avoid unnecessary writes)
        float yawAccelCur = *(float*)(baseAddr + 0x00E8 + 4);
        float pitchAccelCur = *(float*)(baseAddr + 0x0104 + 4);
        float correctionAccelCur = *(float*)(baseAddr + 0x0120 + 4);

        // Only write if values differ from our targets (something reset them or first time)
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

        Log($"  Applied to 0x{baseAddr:X}: Yaw(Spd={Configuration.YawSpeed} Acc={yawAccelCur}->{Configuration.YawAcceleration}) Pitch(Acc={pitchAccelCur}->{Configuration.PitchAcceleration}) Correction(Acc={correctionAccelCur}->{Configuration.CorrectionAcceleration})");
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
        _logger?.WriteLine($"[{_modConfig.ModId}] Config updated. Applying new values.");
        // Force re-apply on next tick
        unsafe { _cachedBehaviors.Clear(); }
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}

using p3rpc.camfix.Configuration;
using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3rpc.camfix;

public class Mod : ModBase
{
    private static readonly byte[] NativeBehaviorClassName =
        System.Text.Encoding.ASCII.GetBytes("FldCameraBehaviorFree");

    private static ILogger? _logger;
    private static IReloadedHooks? _hooks;
    private readonly IModConfig _modConfig;
    public static Config Configuration = null!;

    private static unsafe UnrealTypes.FUObjectArray* _gUObjectArray;
    private static unsafe UnrealTypes.FNamePool* _gNamePool;

    private static nint _nativeBehaviorClass;
    private static nint _staticConstructObjectAddress;
    private static IHook<StaticConstructObjectDelegate>? _staticConstructObjectHook;

    private static readonly object _scanLock = new();
    private static int _globalsResolved;

    [ThreadStatic]
    private static int _constructionDepth;

    [ThreadStatic]
    private static List<nint>? _pendingBehaviorPatches;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate UnrealTypes.UObject* StaticConstructObjectDelegate(
        UnrealTypes.FStaticConstructObjectParameters* parameters);

    public Mod(ModContext context)
    {
        _logger = context.Logger;
        _hooks = context.Hooks;
        _modConfig = context.ModConfig;
        Configuration = context.Configuration;

        if (context.StartupScanner == null)
        {
            LogError("No startup scanner available. Mod will not work.");
            return;
        }

        if (_hooks == null)
        {
            LogError("Reloaded hooks controller is unavailable. Install/enable reloaded.sharedlib.hooks.");
            return;
        }

        var baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;
        Log($"Initializing event-driven camera fix. Base address: 0x{baseAddress:X}");
        ScanRequiredAddresses(context.StartupScanner, baseAddress);
    }

    private unsafe void ScanRequiredAddresses(IStartupScanner scanner, nint baseAddress)
    {
        const string fuObjectArraySig =
            "48 8B 05 ?? ?? ?? ?? 48 8B 0C ?? 48 8D 04 ?? 48 85 C0 74 ?? 44 39 40 ?? 75 ?? F7 40 ?? 00 00 00 30 75 ?? 48 8B 00";
        const string fNamePoolSig =
            "4C 8D 05 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 C6 05 ?? ?? ?? ?? 01 48 8B 44 24 ?? 48 8B D3 48 C1 E8 20 8D 0C ?? 49 03 4C ?? ?? E8 ?? ?? ?? ?? 48 8B C3";
        const string staticConstructObjectSig =
            "48 89 5C 24 ?? 48 89 74 24 ?? 55 57 41 54 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC B0 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 39";

        scanner.AddMainModuleScan(fuObjectArraySig, result =>
        {
            if (!result.Found)
            {
                LogError("FUObjectArray signature not found.");
                return;
            }

            var instruction = baseAddress + result.Offset;
            var objectsField = ResolveRipRelative(instruction);
            _gUObjectArray = (UnrealTypes.FUObjectArray*)(objectsField - 0x10);
            Log($"Found FUObjectArray at 0x{(nint)_gUObjectArray:X}.");
            TryInitialize();
        });

        scanner.AddMainModuleScan(fNamePoolSig, result =>
        {
            if (!result.Found)
            {
                LogError("FNamePool signature not found.");
                return;
            }

            var instruction = baseAddress + result.Offset;
            _gNamePool = (UnrealTypes.FNamePool*)ResolveRipRelative(instruction);
            Log($"Found FNamePool at 0x{(nint)_gNamePool:X}.");
            TryInitialize();
        });

        scanner.AddMainModuleScan(staticConstructObjectSig, result =>
        {
            if (!result.Found)
            {
                LogError("StaticConstructObject_Internal signature not found.");
                return;
            }

            _staticConstructObjectAddress = baseAddress + result.Offset;
            Log($"Found StaticConstructObject_Internal at 0x{_staticConstructObjectAddress:X}.");
            TryInitialize();
        });
    }

    private static unsafe nint ResolveRipRelative(nint instruction)
    {
        int displacement = *(int*)(instruction + 3);
        return instruction + 7 + displacement;
    }

    private static unsafe void TryInitialize()
    {
        if (_gUObjectArray == null || _gNamePool == null || _staticConstructObjectAddress == 0)
            return;
        if (Interlocked.Exchange(ref _globalsResolved, 1) != 0)
            return;

        lock (_scanLock)
        {
            _staticConstructObjectHook = _hooks!
                .CreateHook<StaticConstructObjectDelegate>(
                    StaticConstructObject,
                    _staticConstructObjectAddress);
            _staticConstructObjectHook.Activate();

            Log(
                "Construction hook active. Writes are deferred until the " +
                "outermost camera construction call returns.");
        }
    }

    private static unsafe UnrealTypes.UObject* StaticConstructObject(
        UnrealTypes.FStaticConstructObjectParameters* parameters)
    {
        UnrealTypes.UClass* requestedClass =
            parameters == null ? null : parameters->Class;

        // Almost every UObject takes this path. Once the camera class pointer
        // is resolved, this is only two pointer comparisons before calling the
        // original function. Re-entrancy tracking is limited to camera object
        // construction and does not affect ordinary UObject creation.
        if (!IsCameraBehaviorClass(requestedClass))
            return _staticConstructObjectHook!.OriginalFunction(parameters);

        _constructionDepth++;
        try
        {
            UnrealTypes.UObject* created =
                _staticConstructObjectHook!.OriginalFunction(parameters);

            if (created != null && IsCameraBehaviorClass(created->ClassPrivate))
                QueueBehaviorPatch((nint)created);

            return created;
        }
        finally
        {
            _constructionDepth--;
            if (_constructionDepth == 0)
                FlushDeferredBehaviorPatches();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueBehaviorPatch(nint behavior)
    {
        List<nint> pending = _pendingBehaviorPatches ??= new List<nint>(4);
        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i] == behavior)
                return;
        }

        pending.Add(behavior);
    }

    private static unsafe void FlushDeferredBehaviorPatches()
    {
        List<nint>? pending = _pendingBehaviorPatches;
        if (pending == null || pending.Count == 0)
            return;

        try
        {
            if (!Configuration.Enabled)
                return;

            for (int i = 0; i < pending.Count; i++)
            {
                UnrealTypes.UObject* behavior =
                    (UnrealTypes.UObject*)pending[i];
                if (behavior == null ||
                    !IsCameraBehaviorClass(behavior->ClassPrivate))
                {
                    continue;
                }

                // StaticConstructObject_Internal can return an existing object
                // from a later call. Avoid repeated writes and diagnostics when
                // this object was already patched.
                if (ValuesMatchConfiguration(behavior))
                    continue;

                ApplyConfiguredValues(behavior);
            }
        }
        finally
        {
            pending.Clear();
        }
    }

    private static unsafe void ScanAndApplyToAll(
        string reason,
        bool restoreDefaultsWhenDisabled)
    {
        UnrealTypes.FUObjectArray* array = _gUObjectArray;
        if (array == null || _gNamePool == null || array->Objects == null)
        {
            LogError($"Cannot run {reason} scan because Unreal globals are unavailable.");
            return;
        }

        int count = array->NumElements;
        int chunks = array->NumChunks;
        if (count <= 0 || chunks <= 0)
        {
            LogError($"Cannot run {reason} scan: UObject array is empty.");
            return;
        }

        int matched = 0;
        bool applyFix = Configuration.Enabled;

        for (int chunkIndex = 0; chunkIndex < chunks; chunkIndex++)
        {
            UnrealTypes.FUObjectItem* chunk = array->Objects[chunkIndex];
            if (chunk == null) continue;

            int chunkSize = Math.Min(0x10000, count - chunkIndex * 0x10000);
            for (int itemIndex = 0; itemIndex < chunkSize; itemIndex++)
            {
                UnrealTypes.UObject* obj = chunk[itemIndex].Object;
                if (obj == null || obj->ClassPrivate == null) continue;

                UnrealTypes.UClass* objectClass = obj->ClassPrivate;
                if (!IsCameraBehaviorClass(objectClass)) continue;

                matched++;

                if (applyFix)
                    ApplyConfiguredValues(obj);
                else if (restoreDefaultsWhenDisabled)
                    ApplyGameDefaults(obj);
            }
        }

        Log(
            $"{reason} scan processed {matched} camera behavior object(s): " +
            $"Mode={(applyFix ? "configured values" : restoreDefaultsWhenDisabled ? "game defaults" : "resolve only")}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsCameraBehaviorClass(UnrealTypes.UClass* objectClass)
    {
        if (objectClass == null || _gNamePool == null) return false;

        nint classAddress = (nint)objectClass;
        nint resolvedClass = Volatile.Read(ref _nativeBehaviorClass);
        if (resolvedClass != 0)
            return classAddress == resolvedClass;

        // Name matching only runs until the native class pointer is resolved.
        // Every later construction event takes one pointer-comparison fast path.
        if (_gNamePool->EqualsAnsi(
                objectClass->baseObj.NamePrivate,
                NativeBehaviorClassName))
        {
            Volatile.Write(ref _nativeBehaviorClass, classAddress);
            Log($"Resolved native camera behavior class at 0x{classAddress:X}.");
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ApplyConfiguredValues(UnrealTypes.UObject* behavior)
    {
        nint address = (nint)behavior;
        Config config = Configuration;

        WriteRotParam(
            address + 0x00E8,
            config.YawSpeed,
            config.YawAcceleration,
            config.YawDeceleration,
            config.YawPress,
            config.YawRelease);
        WriteRotParam(
            address + 0x0104,
            config.PitchSpeed,
            config.PitchAcceleration,
            config.PitchDeceleration,
            config.PitchPress,
            config.PitchRelease);
        WriteRotParam(
            address + 0x0120,
            config.CorrectionSpeed,
            config.CorrectionAcceleration,
            config.CorrectionDeceleration,
            config.CorrectionPress,
            config.CorrectionRelease);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ValuesMatchConfiguration(
        UnrealTypes.UObject* behavior)
    {
        nint address = (nint)behavior;
        Config config = Configuration;

        return RotParamMatches(
                   address + 0x00E8,
                   config.YawSpeed,
                   config.YawAcceleration,
                   config.YawDeceleration,
                   config.YawPress,
                   config.YawRelease) &&
               RotParamMatches(
                   address + 0x0104,
                   config.PitchSpeed,
                   config.PitchAcceleration,
                   config.PitchDeceleration,
                   config.PitchPress,
                   config.PitchRelease) &&
               RotParamMatches(
                   address + 0x0120,
                   config.CorrectionSpeed,
                   config.CorrectionAcceleration,
                   config.CorrectionDeceleration,
                   config.CorrectionPress,
                   config.CorrectionRelease);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool RotParamMatches(
        nint address,
        float speed,
        float acceleration,
        float deceleration,
        float press,
        float release)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(*(float*)(address + 0) - speed) <= epsilon &&
               MathF.Abs(*(float*)(address + 4) - acceleration) <= epsilon &&
               MathF.Abs(*(float*)(address + 8) - deceleration) <= epsilon &&
               MathF.Abs(*(float*)(address + 12) - press) <= epsilon &&
               MathF.Abs(*(float*)(address + 16) - release) <= epsilon;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ApplyGameDefaults(UnrealTypes.UObject* behavior)
    {
        nint address = (nint)behavior;
        WriteRotParam(address + 0x00E8, 125.0f, 0.1f, 0.1f, 0.05f, 0.1f);
        WriteRotParam(address + 0x0104, 90.0f, 0.1f, 0.1f, 0.0f, 0.1f);
        WriteRotParam(address + 0x0120, 35.0f, 0.5f, 0.3f, 0.3f, 0.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteRotParam(
        nint address,
        float speed,
        float acceleration,
        float deceleration,
        float press,
        float release)
    {
        *(float*)(address + 0) = speed;
        *(float*)(address + 4) = acceleration;
        *(float*)(address + 8) = deceleration;
        *(float*)(address + 12) = press;
        *(float*)(address + 16) = release;
    }

    private static void Log(string message) =>
        _logger?.WriteLine($"[P3R CamFix] {message}");

    private static void LogError(string message) =>
        _logger?.WriteLine($"[P3R CamFix] {message}", System.Drawing.Color.Red);

    public override void ConfigurationUpdated(Config configuration)
    {
        Configuration = configuration;

        if (Volatile.Read(ref _globalsResolved) == 0)
        {
            _logger?.WriteLine($"[{_modConfig.ModId}] Configuration updated before initialization.");
            return;
        }

        lock (_scanLock)
        {
            ScanAndApplyToAll("configuration update", restoreDefaultsWhenDisabled: true);
        }
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}

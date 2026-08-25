using Microsoft.Win32;
using Reloaded.Mod.Interfaces;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

internal enum GamepadBackend
{
    None,
    XInput,
    GameInput,
}

internal readonly record struct DirectGamepadSnapshot(
    bool Available,
    int RightStickX,
    int RightStickY,
    GamepadBackend Backend);

/// <summary>
/// Owns direct-controller input selection. Backends are sampled independently,
/// but exactly one backend is exposed to camera code at a time. Selection is
/// sticky until the active device disconnects. This prevents a controller that
/// is exposed through both APIs from being processed twice or oscillating
/// between sources.
/// </summary>
internal sealed class GamepadInputRouter : IDisposable
{
    private readonly ILogger _logger;
    private readonly XInputGamepadSource _xInput;
    private readonly GameInputGamepadSource? _gameInput;
    private GamepadBackend _activeBackend;

    public GamepadInputRouter(ILogger logger)
    {
        _logger = logger;
        _xInput = new XInputGamepadSource();
        _gameInput = GameInputGamepadSource.TryCreate(out GameInputGamepadSource? source, out string status)
            ? source
            : null;

        _logger.WriteLine("[P3R CamFix] Direct gamepad polling enabled without controller API hooks.");
        if (_gameInput != null)
            _logger.WriteLine($"[P3R CamFix] GameInput fallback available ({status}).");
        else
            _logger.WriteLine($"[P3R CamFix] GameInput fallback unavailable ({status}); XInput and native game axes remain available.", System.Drawing.Color.Orange);
    }

    public DirectGamepadSnapshot Poll(float activityThreshold)
    {
        GamepadSourceSample xInput = _xInput.Poll(activityThreshold);
        GamepadSourceSample gameInput = _gameInput?.Poll(activityThreshold) ?? default;

        GamepadBackend next = SelectBackend(_activeBackend, xInput, gameInput);
        if (next != _activeBackend)
        {
            GamepadBackend previous = _activeBackend;
            _activeBackend = next;
            if (next != GamepadBackend.None)
                _logger.WriteLine($"[P3R CamFix] Direct gamepad source: {next}.");
            else if (previous != GamepadBackend.None)
                _logger.WriteLine("[P3R CamFix] Direct gamepad source disconnected; using the game's native camera axis.", System.Drawing.Color.Orange);
        }

        GamepadSourceSample selected = next switch
        {
            GamepadBackend.XInput => xInput,
            GamepadBackend.GameInput => gameInput,
            _ => default,
        };

        return selected.Available
            ? new DirectGamepadSnapshot(true, ToRawStick(selected.State.RightStickX), ToRawStick(selected.State.RightStickY), next)
            : default;
    }

    internal static GamepadBackend SelectBackend(
        GamepadBackend active,
        in GamepadSourceSample xInput,
        in GamepadSourceSample gameInput)
    {
        if (active == GamepadBackend.XInput && xInput.Available)
            return GamepadBackend.XInput;

        if (active == GamepadBackend.GameInput && gameInput.Available)
            return GamepadBackend.GameInput;

        if (xInput.DeliberateActivity && xInput.Available)
            return GamepadBackend.XInput;
        if (gameInput.DeliberateActivity && gameInput.Available)
            return GamepadBackend.GameInput;

        // Source-local selection only becomes available after deliberate input,
        // so a connected but untouched controller cannot claim the camera.
        if (xInput.Available)
            return GamepadBackend.XInput;
        if (gameInput.Available)
            return GamepadBackend.GameInput;
        return GamepadBackend.None;
    }

    private static int ToRawStick(float value)
    {
        value = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        return value >= 0f
            ? (int)MathF.Round(value * 32767f)
            : (int)MathF.Round(value * 32768f);
    }

    public void Dispose()
    {
        _gameInput?.Dispose();
        _xInput.Dispose();
    }
}

internal readonly record struct GamepadSourceSample(
    bool Available,
    bool DeliberateActivity,
    NormalizedGamepadState State);

internal readonly record struct NormalizedGamepadState(
    ulong Buttons,
    float LeftTrigger,
    float RightTrigger,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY)
{
    public bool HasDeliberateActivity(in NormalizedGamepadState previous, bool havePrevious, float threshold)
    {
        threshold = Math.Clamp(threshold, 0.02f, 0.5f);
        if (!havePrevious)
            return Buttons != 0 || LeftTrigger >= 0.1f || RightTrigger >= 0.1f ||
                   VectorLength(LeftStickX, LeftStickY) >= threshold ||
                   VectorLength(RightStickX, RightStickY) >= threshold;

        bool buttonPressed = (Buttons & ~previous.Buttons) != 0;
        bool triggerMoved = (LeftTrigger >= 0.1f && Math.Abs(LeftTrigger - previous.LeftTrigger) >= 0.01f) ||
                            (RightTrigger >= 0.1f && Math.Abs(RightTrigger - previous.RightTrigger) >= 0.01f);
        bool leftStickMoved = VectorLength(LeftStickX, LeftStickY) >= threshold &&
                              VectorLength(LeftStickX - previous.LeftStickX, LeftStickY - previous.LeftStickY) >= 0.01f;
        bool rightStickMoved = VectorLength(RightStickX, RightStickY) >= threshold &&
                               VectorLength(RightStickX - previous.RightStickX, RightStickY - previous.RightStickY) >= 0.01f;
        return buttonPressed || triggerMoved || leftStickMoved || rightStickMoved;
    }

    private static float VectorLength(float x, float y) => MathF.Sqrt((x * x) + (y * y));
}

internal sealed unsafe class XInputGamepadSource : IDisposable
{
    private const int UserCount = 4;
    private readonly NormalizedGamepadState[] _previous = new NormalizedGamepadState[UserCount];
    private readonly bool[] _havePrevious = new bool[UserCount];
    private XInputGetStateDelegate? _getState;
    private int _activeUser = -1;

    public GamepadSourceSample Poll(float activityThreshold)
    {
        ResolveApi();
        if (_getState == null)
            return default;

        int activityUser = -1;
        NormalizedGamepadState activityState = default;
        bool activeConnected = false;
        NormalizedGamepadState activeState = default;

        for (int user = 0; user < UserCount; user++)
        {
            XInputState native = default;
            uint result = _getState((uint)user, &native);
            if (result != 0)
            {
                _havePrevious[user] = false;
                if (user == _activeUser)
                    activeConnected = false;
                continue;
            }

            NormalizedGamepadState state = Normalize(native.Gamepad);
            bool activity = state.HasDeliberateActivity(_previous[user], _havePrevious[user], activityThreshold);
            _previous[user] = state;
            _havePrevious[user] = true;

            if (user == _activeUser)
            {
                activeConnected = true;
                activeState = state;
            }
            if (activity)
            {
                activityUser = user;
                activityState = state;
            }
        }

        if (activityUser >= 0)
        {
            _activeUser = activityUser;
            return new GamepadSourceSample(true, true, activityState);
        }

        if (_activeUser >= 0 && activeConnected)
            return new GamepadSourceSample(true, false, activeState);

        _activeUser = -1;
        return default;
    }

    private void ResolveApi()
    {
        if (_getState != null)
            return;

        nint module = Native.GetModuleHandleW("XINPUT1_3.dll");
        if (module == 0)
            return;
        nint address = Native.GetProcAddress(module, "XInputGetState");
        if (address != 0)
            _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(address);
    }

    private static NormalizedGamepadState Normalize(in XInputGamepad gamepad) => new(
        gamepad.Buttons,
        gamepad.LeftTrigger / 255f,
        gamepad.RightTrigger / 255f,
        NormalizeStick(gamepad.LeftThumbX),
        NormalizeStick(gamepad.LeftThumbY),
        NormalizeStick(gamepad.RightThumbX),
        NormalizeStick(gamepad.RightThumbY));

    private static float NormalizeStick(short value) => value >= 0 ? value / 32767f : value / 32768f;

    public void Dispose() { }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate uint XInputGetStateDelegate(uint userIndex, XInputState* state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern nint GetProcAddress(nint module, string procName);
    }
}

internal sealed unsafe class GameInputGamepadSource : IDisposable
{
    private const uint GameInputKindGamepad = 0x00040000;
    private static readonly Guid GameInputV0InterfaceId = new("11BE2A7E-4254-445A-9C09-FFC40F006918");

    private readonly nint _module;
    private readonly Dictionary<nint, NormalizedGamepadState> _previous = new();
    private nint _gameInput;
    private nint _activeDevice;

    private GameInputGamepadSource(nint module, nint gameInput)
    {
        _module = module;
        _gameInput = gameInput;
    }

    public static bool TryCreate(out GameInputGamepadSource? source, out string status)
    {
        source = null;
        status = "runtime not found";
        foreach ((string path, string label) in EnumerateRuntimeCandidates())
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out nint module))
                continue;

            nint gameInput = 0;
            int result = CreateInterface(module, &gameInput);
            if (result >= 0 && gameInput != 0)
            {
                source = new GameInputGamepadSource(module, gameInput);
                status = label;
                return true;
            }

            NativeLibrary.Free(module);
            status = $"{label}, HRESULT 0x{result:X8}";
        }
        return false;
    }

    public GamepadSourceSample Poll(float activityThreshold)
    {
        if (_gameInput == 0)
            return default;

        nint reading = 0;
        int result = GetCurrentReading(_gameInput, GameInputKindGamepad, _activeDevice, &reading);
        if (result < 0 || reading == 0)
        {
            Release(ref _activeDevice);
            return default;
        }

        try
        {
            GameInputGamepadState native = default;
            if (GetGamepadState(reading, &native) == 0)
                return default;

            nint device = 0;
            GetDevice(reading, &device);
            if (device == 0)
                return default;

            try
            {
                NormalizedGamepadState state = new(
                    native.Buttons,
                    native.LeftTrigger,
                    native.RightTrigger,
                    native.LeftThumbstickX,
                    native.LeftThumbstickY,
                    native.RightThumbstickX,
                    native.RightThumbstickY);
                bool havePrevious = _previous.TryGetValue(device, out NormalizedGamepadState previous);
                bool activity = state.HasDeliberateActivity(previous, havePrevious, activityThreshold);
                _previous[device] = state;

                if (_activeDevice == 0 && activity)
                {
                    _activeDevice = device;
                    device = 0; // Retain the GetDevice reference while selected.
                }

                return new GamepadSourceSample(_activeDevice != 0, activity, state);
            }
            finally
            {
                Release(ref device);
            }
        }
        finally
        {
            Release(ref reading);
        }
    }

    private static IEnumerable<(string Path, string Label)> EnumerateRuntimeCandidates()
    {
        string systemDirectory = Environment.SystemDirectory;
        yield return (Path.Combine(systemDirectory, "GameInputRedist.dll"), "GameInput redistributable");

        string? redistDirectory = null;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\GameInput");
            redistDirectory = key?.GetValue("RedistDir") as string;
        }
        catch
        {
            // Registry lookup is optional; the inbox runtime remains available.
        }

        if (!string.IsNullOrWhiteSpace(redistDirectory))
            yield return (Path.Combine(redistDirectory, "GameInputRedist.dll"), "GameInput redistributable");
        yield return (Path.Combine(systemDirectory, "GameInput.dll"), "Windows inbox GameInput");
    }

    private static int CreateInterface(nint module, nint* gameInput)
    {
        if (NativeLibrary.TryGetExport(module, "GameInputInitialize", out nint initializeAddress))
        {
            GameInputInitializeDelegate initialize = Marshal.GetDelegateForFunctionPointer<GameInputInitializeDelegate>(initializeAddress);
            Guid interfaceId = GameInputV0InterfaceId;
            return initialize(&interfaceId, gameInput);
        }
        if (NativeLibrary.TryGetExport(module, "GameInputCreate", out nint createAddress))
        {
            GameInputCreateDelegate create = Marshal.GetDelegateForFunctionPointer<GameInputCreateDelegate>(createAddress);
            return create(gameInput);
        }
        return unchecked((int)0x8007007F); // HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)
    }

    private static int GetCurrentReading(nint gameInput, uint kind, nint device, nint* reading)
    {
        nint* vtable = *(nint**)gameInput;
        var function = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)vtable[4];
        return function(gameInput, kind, device, reading);
    }

    private static void GetDevice(nint reading, nint* device)
    {
        nint* vtable = *(nint**)reading;
        var function = (delegate* unmanaged[Stdcall]<nint, nint*, void>)vtable[6];
        function(reading, device);
    }

    private static byte GetGamepadState(nint reading, GameInputGamepadState* state)
    {
        nint* vtable = *(nint**)reading;
        var function = (delegate* unmanaged[Stdcall]<nint, GameInputGamepadState*, byte>)vtable[22];
        return function(reading, state);
    }

    private static void Release(ref nint value)
    {
        nint current = value;
        value = 0;
        if (current == 0)
            return;
        nint* vtable = *(nint**)current;
        var function = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        function(current);
    }

    public void Dispose()
    {
        Release(ref _activeDevice);
        Release(ref _gameInput);
        if (_module != 0)
            NativeLibrary.Free(_module);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int GameInputInitializeDelegate(Guid* interfaceId, nint* gameInput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int GameInputCreateDelegate(nint* gameInput);

    [StructLayout(LayoutKind.Sequential)]
    private struct GameInputGamepadState
    {
        public uint Buttons;
        public float LeftTrigger;
        public float RightTrigger;
        public float LeftThumbstickX;
        public float LeftThumbstickY;
        public float RightThumbstickX;
        public float RightThumbstickY;
    }
}

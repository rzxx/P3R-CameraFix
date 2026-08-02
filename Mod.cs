using p3rpc.camfix.Configuration;
using p3rpc.camfix.Template;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;

namespace p3rpc.camfix;

public class Mod : ModBase
{
    private static ILogger? _logger;
    private readonly IModConfig _modConfig;
    public static Config Configuration = null!;

    private readonly CameraFilterTrace? _cameraFilterTrace;
    private readonly RawInputTrace? _rawInputTrace;
    private readonly FixedCameraTrace? _fixedCameraTrace;
    private readonly ExperimentalSplineCamera? _experimentalSplineCamera;
    private readonly ExperimentalFreeCamera? _experimentalFreeCamera;

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

        if (Configuration.Enabled)
        {
            if (context.Hooks == null)
            {
                LogError("Responsive camera input requested, but Reloaded.Hooks is unavailable.");
            }
            else
            {
                if (Configuration.EnableSplineCameraFix ||
                    Configuration.EnableFreeCameraFix ||
                    Configuration.EnableCameraTransitionTrace ||
                    Configuration.EnableSplineCameraTrace ||
                    Configuration.EnableFreeCameraTrace)
                {
                    _experimentalSplineCamera = new ExperimentalSplineCamera(context, baseAddress);
                }

                // Native behavior parameters are applied from this live
                // camera callback even when replacement input is disabled.
                _experimentalFreeCamera = new ExperimentalFreeCamera(
                    context, baseAddress, _experimentalSplineCamera);
            }
        }

        if (Configuration.EnableNativeCameraFilterTrace)
        {
            if (context.Hooks == null)
            {
                LogError("Camera filter trace requested, but Reloaded.Hooks is unavailable.");
            }
            else
            {
                _cameraFilterTrace = new CameraFilterTrace(context, baseAddress);
            }
        }

        if (Configuration.EnableRawInputTrace)
        {
            if (context.Hooks == null)
            {
                LogError("Raw input trace requested, but Reloaded.Hooks is unavailable.");
            }
            else
            {
                _rawInputTrace = new RawInputTrace(context);
            }
        }

        if (Configuration.EnableLiteralFixedCameraTrace)
        {
            if (context.Hooks == null)
            {
                LogError("Fixed camera trace requested, but Reloaded.Hooks is unavailable.");
            }
            else
            {
                _fixedCameraTrace = new FixedCameraTrace(context, baseAddress);
            }
        }
    }

    private static void Log(string msg) => _logger?.WriteLine($"[P3R CamFix] {msg}");
    private static void LogError(string msg) => _logger?.WriteLine($"[P3R CamFix] {msg}", System.Drawing.Color.Red);

    public override void ConfigurationUpdated(Config configuration)
    {
        Configuration = configuration;
        _logger?.WriteLine(
            $"[{_modConfig.ModId}] Config updated. Native camera parameters apply on the next camera update; hook and trace options require a restart.");
    }

    public override void Disposing()
    {
        _experimentalFreeCamera?.Dispose();
        _experimentalSplineCamera?.Dispose();
        _fixedCameraTrace?.Dispose();
        _rawInputTrace?.Dispose();
        _cameraFilterTrace?.Dispose();
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}

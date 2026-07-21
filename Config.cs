using p3rpc.camfix.Template.Configuration;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace p3rpc.camfix.Configuration;

public class Config : Configurable<Config>
{
    // The numbered categories keep the normal testing controls first in
    // Reloaded-II while leaving implementation details available to power users.

    [DisplayName("Enable Camera Fix")]
    [Category("01 - Camera")]
    [Description("Master switch for the responsive free- and spline-camera replacements. Restart the game after changing this setting.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Mouse Horizontal Sensitivity")]
    [Category("01 - Camera")]
    [Description("Horizontal mouse sensitivity for the free camera. Spline cameras inherit this base before applying their multiplier. Default: 100%.")]
    [DefaultValue(100)]
    public int MouseHorizontalSensitivityPercent { get; set; } = 100;

    [DisplayName("Mouse Vertical Sensitivity")]
    [Category("01 - Camera")]
    [Description("Vertical mouse sensitivity for the free camera. Spline cameras inherit this base before applying their multiplier. Default: 100%.")]
    [DefaultValue(100)]
    public int MouseVerticalSensitivityPercent { get; set; } = 100;

    [DisplayName("Gamepad Horizontal Turn Speed")]
    [Category("01 - Camera")]
    [Description("Maximum horizontal free-camera turn speed. At 100%, this is also the spline-camera turn-rate limit and does not reduce its angle range. Default: 165 degrees/second.")]
    [DefaultValue(165)]
    public int GamepadHorizontalSpeed { get; set; } = 165;

    [DisplayName("Gamepad Vertical Turn Speed")]
    [Category("01 - Camera")]
    [Description("Maximum vertical free-camera turn speed. At 100%, this is also the spline-camera turn-rate limit and does not reduce its angle range. Default: 100 degrees/second.")]
    [DefaultValue(100)]
    public int GamepadVerticalSpeed { get; set; } = 100;

    [DisplayName("Spline Mouse Sensitivity Multiplier")]
    [Category("01 - Camera")]
    [Description("Mouse sensitivity in spline/rail cameras relative to the free-camera base. 50% is half speed. Default: 50%.")]
    [DefaultValue(50)]
    public int SplineMouseSensitivityPercent { get; set; } = 50;

    [DisplayName("Spline Gamepad Turn-Speed Multiplier")]
    [Category("01 - Camera")]
    [Description("Spline/rail camera turn-speed limit relative to the free-camera degrees-per-second base. This never reduces the reachable angle range. Default: 25%.")]
    [DefaultValue(25)]
    public int SplineGamepadSensitivityPercent { get; set; } = 25;

    [DisplayName("Invert Mouse Y")]
    [Category("01 - Camera")]
    [Description("Reverses vertical mouse movement in both free and spline cameras.")]
    [DefaultValue(false)]
    public bool InvertMouseY { get; set; } = false;

    [DisplayName("Enable Free-Camera Replacement")]
    [Category("02 - Advanced Input")]
    [Description("Enables direct mouse and gamepad input for the normal third-person camera while preserving P3R's collision, correction, and pitch limits. Restart required.")]
    [DefaultValue(true)]
    public bool EnableFreeCameraFix { get; set; } = true;

    [DisplayName("Enable Spline-Camera Replacement")]
    [Category("02 - Advanced Input")]
    [Description("Enables the responsive input replacement for constrained spline/rail cameras. Literal fixed cameras are not modified. Restart required.")]
    [DefaultValue(true)]
    public bool EnableSplineCameraFix { get; set; } = true;

    [DisplayName("Use Raw Mouse Input")]
    [Category("02 - Advanced Input")]
    [Description("Uses physical mouse counts directly instead of P3R's center-warped, stick-like mouse path.")]
    [DefaultValue(true)]
    public bool EnableRawMouse { get; set; } = true;

    [DisplayName("Use Direct Gamepad Input")]
    [Category("02 - Advanced Input")]
    [Description("Reads the right stick before P3R's large upstream deadzone and remap.")]
    [DefaultValue(true)]
    public bool EnableDirectController { get; set; } = true;

    [DisplayName("Gamepad Deadzone")]
    [Category("02 - Advanced Input")]
    [Description("Shared radial right-stick deadzone. Default: 8%.")]
    [DefaultValue(8)]
    public int GamepadDeadzonePercent { get; set; } = 8;

    [DisplayName("Camera Response Curve")]
    [Category("02 - Advanced Input")]
    [Description("How right-stick travel becomes camera turn demand. Standard is recommended.")]
    [DefaultValue(CameraResponseCurvePreset.Standard)]
    public CameraResponseCurvePreset CameraResponseCurve { get; set; } = CameraResponseCurvePreset.Standard;

    [DisplayName("Custom Camera Curve Exponent")]
    [Category("02 - Advanced Input")]
    [Description("Power exponent used only when Camera Response Curve is Custom. 1 is linear; higher values reserve more stick range for precise camera corrections.")]
    [DefaultValue(1.7f)]
    public float CustomCameraCurveExponent { get; set; } = 1.7f;

    [DisplayName("Custom Camera Low-End Calm (Toe)")]
    [Category("02 - Advanced Input")]
    [Description("Additional low-end shaping used only by the Custom curve. Higher values keep small stick movement calmer without increasing the deadzone.")]
    [DefaultValue(0)]
    public int CustomCameraCurveToePercent { get; set; }

    [DisplayName("Custom Camera High-End Reach (Shoulder)")]
    [Category("02 - Advanced Input")]
    [Description("Additional high-end shaping used only by the Custom curve. Higher values pull large stick movement toward full output sooner.")]
    [DefaultValue(0)]
    public int CustomCameraCurveShoulderPercent { get; set; }

    [DisplayName("Spline Small-Movement Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant for suppressing small unavoidable changes in stick turn demand. Default: 0.22 seconds; 0 is fully direct.")]
    [DefaultValue(0.22f)]
    public float SplineControllerSmallSmoothing { get; set; } = 0.22f;

    [DisplayName("Spline Large-Movement Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant for deliberate large changes in stick turn demand. Default: 0.08 seconds.")]
    [DefaultValue(0.08f)]
    public float SplineControllerLargeSmoothing { get; set; } = 0.08f;

    [DisplayName("Spline Stick-Release Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant used to ease the spline controller offset toward center when the stick returns to center. Default: 0.15 seconds.")]
    [DefaultValue(0.15f)]
    public float SplineControllerRecenterSmoothing { get; set; } = 0.15f;

    [DisplayName("Spline Large-Change Threshold")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Turn-demand distance at which response reaches the fast large-movement smoothing value. Default: 0.35.")]
    [DefaultValue(0.35f)]
    public float SplineControllerLargeChangeThreshold { get; set; } = 0.35f;

    [DisplayName("Spline Stick Hysteresis")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Ignores turn-demand fluctuations smaller than this normalized distance. Default: 0.01; 0 disables it.")]
    [DefaultValue(0.01f)]
    public float SplineControllerTargetHysteresis { get; set; } = 0.01f;

    [DisplayName("Spline Mouse Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Optional mouse smoothing time constant. Default: 0 for direct raw response. Any nonzero value intentionally adds latency.")]
    [DefaultValue(0.0f)]
    public float SplineMouseSmoothing { get; set; } = 0.0f;

    [DisplayName("Recenter During Native Input Locks")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Smoothly restores authored center framing when P3R locks camera input for dialogue, menus, and transitions.")]
    [DefaultValue(true)]
    public bool EnableSplineNativeLockRecenter { get; set; } = true;

    [DisplayName("Spline Recenter Duration")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Duration of the coordinated autonomous return to center. Default: 0.5 seconds.")]
    [DefaultValue(0.5f)]
    public float SplineAutonomousRecenterDuration { get; set; } = 0.5f;

    [DisplayName("Recenter Idle Mouse During Rail Motion")]
    [Category("03 - Advanced Spline Camera")]
    [Description("After mouse input stops, recenters only while the authored rail camera is moving. Stationary glances remain held.")]
    [DefaultValue(true)]
    public bool EnableSplineMouseIdleRecenter { get; set; } = true;

    [DisplayName("Spline Mouse Idle Delay")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Seconds without accepted mouse movement before rail motion may trigger recentering. Default: 2.5.")]
    [DefaultValue(2.5f)]
    public float SplineMouseIdleRecenterDelay { get; set; } = 2.5f;

    [DisplayName("Yaw Acceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native free-camera acceleration time. Default: 0 for immediate response.")]
    [DefaultValue(0.0f)]
    public float YawAcceleration { get; set; } = 0.0f;

    [DisplayName("Yaw Deceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native free-camera deceleration time. Default: 0.")]
    [DefaultValue(0.0f)]
    public float YawDeceleration { get; set; } = 0.0f;

    [DisplayName("Yaw Press Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native delay before horizontal rotation starts. Default: 0.")]
    [DefaultValue(0.0f)]
    public float YawPress { get; set; } = 0.0f;

    [DisplayName("Yaw Release Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native horizontal release delay. Default: 0.")]
    [DefaultValue(0.0f)]
    public float YawRelease { get; set; } = 0.0f;

    [DisplayName("Pitch Acceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native free-camera acceleration time. Default: 0.")]
    [DefaultValue(0.0f)]
    public float PitchAcceleration { get; set; } = 0.0f;

    [DisplayName("Pitch Deceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native free-camera deceleration time. Default: 0.")]
    [DefaultValue(0.0f)]
    public float PitchDeceleration { get; set; } = 0.0f;

    [DisplayName("Pitch Press Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native delay before vertical rotation starts. Default: 0.")]
    [DefaultValue(0.0f)]
    public float PitchPress { get; set; } = 0.0f;

    [DisplayName("Pitch Release Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native vertical release delay. Default: 0.")]
    [DefaultValue(0.0f)]
    public float PitchRelease { get; set; } = 0.0f;

    [DisplayName("Auto-Correction Speed")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native camera-follow correction speed. Default: 35.")]
    [DefaultValue(35.0f)]
    public float CorrectionSpeed { get; set; } = 35.0f;

    [DisplayName("Auto-Correction Acceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native correction acceleration time. Default: 0.5 seconds.")]
    [DefaultValue(0.5f)]
    public float CorrectionAcceleration { get; set; } = 0.5f;

    [DisplayName("Auto-Correction Deceleration")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native correction deceleration time. Default: 0.3 seconds.")]
    [DefaultValue(0.3f)]
    public float CorrectionDeceleration { get; set; } = 0.3f;

    [DisplayName("Auto-Correction Press Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native correction activation delay. Default: 0.3 seconds.")]
    [DefaultValue(0.3f)]
    public float CorrectionPress { get; set; } = 0.3f;

    [DisplayName("Auto-Correction Release Delay")]
    [Category("04 - Advanced Native Camera")]
    [Description("Native correction release delay. Default: 0.")]
    [DefaultValue(0.0f)]
    public float CorrectionRelease { get; set; } = 0.0f;

    [DisplayName("Trace Free Camera")]
    [Category("99 - Debug")]
    [Description("Writes high-volume free-camera telemetry. Restart required. Leave disabled for normal play.")]
    [DefaultValue(false)]
    public bool EnableFreeCameraTrace { get; set; } = false;

    [DisplayName("Trace Spline Camera")]
    [Category("99 - Debug")]
    [Description("Writes high-volume spline-camera telemetry. Restart required. Leave disabled for normal play.")]
    [DefaultValue(false)]
    public bool EnableSplineCameraTrace { get; set; } = false;

    [DisplayName("Trace Camera Transitions and UI")]
    [Category("99 - Debug")]
    [Description("Writes high-volume camera, fade, UI-ownership, and cursor telemetry. Press Page Up to mark an observed problem. Restart required. Leave disabled for normal play.")]
    [DefaultValue(false)]
    public bool EnableCameraTransitionTrace { get; set; } = false;

    [DisplayName("Trace Native Camera Filter")]
    [Category("99 - Debug")]
    [Description("Hooks and traces P3R's original camera-axis filter. Restart required.")]
    [DefaultValue(false)]
    public bool EnableNativeCameraFilterTrace { get; set; } = false;

    [DisplayName("Trace Raw Input")]
    [Category("99 - Debug")]
    [Description("Writes raw and native mouse input, cursor warp, device registration, and XInput telemetry. Restart required.")]
    [DefaultValue(false)]
    public bool EnableRawInputTrace { get; set; } = false;

    [DisplayName("Trace Literal Fixed Cameras")]
    [Category("99 - Debug")]
    [Description("Research trace for literal fixed cameras and their owning field-camera operation. This does not refer to spline cameras. Restart required.")]
    [DefaultValue(false)]
    public bool EnableLiteralFixedCameraTrace { get; set; } = false;

    [DisplayName("Maximum Trace Samples")]
    [Category("99 - Debug")]
    [Description("Maximum records allocated by each enabled trace. No trace buffers are allocated when all debug traces are disabled.")]
    [DefaultValue(262144)]
    public int TraceCapacity { get; set; } = 262144;

    public float GetCameraCurveExponent() => CameraResponseCurve switch
    {
        CameraResponseCurvePreset.Direct => 1.0f,
        CameraResponseCurvePreset.Comfort => 1.7f,
        CameraResponseCurvePreset.Dynamic => 1.15f,
        CameraResponseCurvePreset.Custom => Math.Clamp(CustomCameraCurveExponent, 1f, 3f),
        _ => 1.35f,
    };

    public float GetCameraCurveToeStrength() => CameraResponseCurve switch
    {
        CameraResponseCurvePreset.Comfort => 0.1f,
        CameraResponseCurvePreset.Dynamic => 0.4f,
        CameraResponseCurvePreset.Custom => Math.Clamp(CustomCameraCurveToePercent, 0, 100) / 100f,
        _ => 0f,
    };

    public float GetCameraCurveShoulderStrength() => CameraResponseCurve switch
    {
        CameraResponseCurvePreset.Comfort => 0.25f,
        CameraResponseCurvePreset.Dynamic => 0.4f,
        CameraResponseCurvePreset.Custom => Math.Clamp(CustomCameraCurveShoulderPercent, 0, 100) / 100f,
        _ => 0f,
    };

    public float ApplyCameraResponseCurve(float normalizedMagnitude) => ApplyCameraResponseCurve(
        normalizedMagnitude,
        GetCameraCurveExponent(),
        GetCameraCurveToeStrength(),
        GetCameraCurveShoulderStrength());

    internal static float ApplyCameraResponseCurve(
        float normalizedMagnitude, float exponent, float toeStrength, float shoulderStrength)
    {
        float input = Math.Clamp(normalizedMagnitude, 0f, 1f);
        float output = MathF.Pow(input, Math.Clamp(exponent, 1f, 3f));
        float toe = Math.Clamp(toeStrength, 0f, 1f);
        float shoulder = Math.Clamp(shoulderStrength, 0f, 1f);

        // Monotonic endpoint shaping. The cubic weights localize toe influence
        // near zero and shoulder influence near one while preserving exact
        // endpoints. Each transform has a non-negative derivative for strengths
        // in [0, 1], so their composition cannot create reversals.
        float inverse = 1f - output;
        output -= toe * output * inverse * inverse * inverse;
        output += shoulder * output * output * output * (1f - output);
        return Math.Clamp(output, 0f, 1f);
    }
}

public enum CameraResponseCurvePreset
{
    [Display(Name = "Standard (Recommended)")]
    Standard,

    [Display(Name = "Comfort")]
    Comfort,

    [Display(Name = "Direct (Linear)")]
    Direct,

    [Display(Name = "Dynamic (S-Curve)")]
    Dynamic,

    [Display(Name = "Custom")]
    Custom,
}

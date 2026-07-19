using p3rpc.camfix.Template.Configuration;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace p3rpc.camfix.Configuration;

public class Config : Configurable<Config>, IJsonOnDeserialized
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

    [DisplayName("Gamepad Horizontal Sensitivity")]
    [Category("01 - Camera")]
    [Description("Maximum horizontal free-camera turn speed. Spline cameras inherit this base before applying their multiplier. Default: 165 degrees/second.")]
    [DefaultValue(165)]
    public int GamepadHorizontalSpeed { get; set; } = 165;

    [DisplayName("Gamepad Vertical Sensitivity")]
    [Category("01 - Camera")]
    [Description("Maximum vertical free-camera turn speed. Spline cameras inherit this base before applying their multiplier. Default: 100 degrees/second.")]
    [DefaultValue(100)]
    public int GamepadVerticalSpeed { get; set; } = 100;

    [DisplayName("Spline Mouse Sensitivity Multiplier")]
    [Category("01 - Camera")]
    [Description("Mouse sensitivity in spline/rail cameras relative to the free-camera base. 50% is half speed. Default: 100%.")]
    [DefaultValue(100)]
    public int SplineMouseSensitivityPercent { get; set; } = 100;

    [DisplayName("Spline Gamepad Sensitivity Multiplier")]
    [Category("01 - Camera")]
    [Description("Gamepad sensitivity in spline/rail cameras relative to the free-camera base. Default: 100%.")]
    [DefaultValue(100)]
    public int SplineGamepadSensitivityPercent { get; set; } = 100;

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
    [Description("Shared radial right-stick deadzone. Default: 3%.")]
    [DefaultValue(3)]
    public int GamepadDeadzonePercent { get; set; } = 3;

    [DisplayName("Gamepad Response Curve")]
    [Category("02 - Advanced Input")]
    [Description("An ordinary radial power curve. Every preset reaches full output at full stick; calmer curves reserve more range for precise corrections.")]
    [DefaultValue(GamepadCurvePreset.Balanced)]
    public GamepadCurvePreset GamepadResponseCurve { get; set; } = GamepadCurvePreset.Balanced;

    [DisplayName("Custom Curve Exponent")]
    [Category("02 - Advanced Input")]
    [Description("Power exponent used only when Gamepad Response Curve is Custom. 1 is linear; higher values reserve more stick range for precise corrections.")]
    [DefaultValue(1.7f)]
    public float CustomGamepadCurveExponent { get; set; } = 1.7f;

    [DisplayName("Spline Small-Movement Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant for suppressing small unavoidable stick fluctuations. Default: 0.11 seconds; 0 is fully direct.")]
    [DefaultValue(0.11f)]
    public float SplineControllerSmallSmoothing { get; set; } = 0.11f;

    [DisplayName("Spline Large-Movement Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant for deliberate large stick changes. Default: 0.04 seconds.")]
    [DefaultValue(0.04f)]
    public float SplineControllerLargeSmoothing { get; set; } = 0.04f;

    [DisplayName("Spline Stick-Release Smoothing")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Time constant used when the stick returns to center. Default: 0.075 seconds.")]
    [DefaultValue(0.075f)]
    public float SplineControllerRecenterSmoothing { get; set; } = 0.075f;

    [DisplayName("Spline Large-Change Threshold")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Target distance at which response reaches the fast large-movement smoothing value. Default: 0.35.")]
    [DefaultValue(0.35f)]
    public float SplineControllerLargeChangeThreshold { get; set; } = 0.35f;

    [DisplayName("Spline Stick Hysteresis")]
    [Category("03 - Advanced Spline Camera")]
    [Description("Ignores target fluctuations smaller than this normalized distance. Default: 0.01; 0 disables it.")]
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

    [DisplayName("Reject Game Cursor-Warp Packets")]
    [Category("05 - Advanced Compatibility")]
    [Description("Rejects raw mouse packets only when they match a recent large game-driven SetCursorPos warp.")]
    [DefaultValue(true)]
    public bool EnableSplineCursorWarpRejection { get; set; } = true;

    [DisplayName("Cursor-Warp Detection Threshold")]
    [Category("05 - Advanced Compatibility")]
    [Description("Minimum game cursor-warp delta eligible for raw-packet matching. Default: 128 counts.")]
    [DefaultValue(128)]
    public int SplineCursorWarpMinimumCounts { get; set; } = 128;

    [DisplayName("Cursor-Warp Match Tolerance")]
    [Category("05 - Advanced Compatibility")]
    [Description("Per-axis tolerance when matching a raw packet to a recent game cursor warp. Default: 8 counts.")]
    [DefaultValue(8)]
    public int SplineCursorWarpMatchTolerance { get; set; } = 8;

    [DisplayName("Hide Erroneous Gameplay Cursor")]
    [Category("05 - Advanced Compatibility")]
    [Description("Suppresses P3R's erroneous Windows arrow during active spline gameplay while retaining native dialogue and UI ownership.")]
    [DefaultValue(true)]
    public bool EnableSplineGameplayCursorGuard { get; set; } = true;

    [DisplayName("Hide Cursor During Native Fades")]
    [Category("05 - Advanced Compatibility")]
    [Description("Uses P3R's native fade and UI ownership state to suppress transition cursor flashes in free and spline cameras. Restart required.")]
    [DefaultValue(true)]
    public bool EnableNativeFadeCursorGuard { get; set; } = true;

    [DisplayName("Native Fade Boundary Bridge")]
    [Category("05 - Advanced Compatibility")]
    [Description("Bridges measured operation-sampling gaps immediately around native fades. Cursor-only. Default: 0.075 seconds.")]
    [DefaultValue(0.075f)]
    public float NativeFadeCursorBridgeSeconds { get; set; } = 0.075f;

    [DisplayName("Enable Legacy Mouse Fallback")]
    [Category("05 - Advanced Compatibility")]
    [Description("Uses P3R's legacy mouse axis temporarily if raw input is unavailable after a menu or device switch.")]
    [DefaultValue(true)]
    public bool EnableSplineLegacyMouseFallback { get; set; } = true;

    [DisplayName("Legacy Mouse Horizontal Speed")]
    [Category("05 - Advanced Compatibility")]
    [Description("Fallback-only horizontal angular speed. Default: 150 degrees per second.")]
    [DefaultValue(150.0f)]
    public float SplineLegacyMouseYawSpeed { get; set; } = 150.0f;

    [DisplayName("Legacy Mouse Vertical Speed")]
    [Category("05 - Advanced Compatibility")]
    [Description("Fallback-only vertical angular speed. Default: 90 degrees per second.")]
    [DefaultValue(90.0f)]
    public float SplineLegacyMousePitchSpeed { get; set; } = 90.0f;

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
    [Description("Writes high-volume camera, fade, UI-ownership, and cursor telemetry. Restart required. Leave disabled for normal play.")]
    [DefaultValue(false)]
    public bool EnableCameraTransitionTrace { get; set; } = false;

    [DisplayName("Trace Native Camera Filter")]
    [Category("99 - Debug")]
    [Description("Hooks and traces P3R's original camera-axis filter. Restart required.")]
    [DefaultValue(false)]
    public bool EnableNativeCameraFilterTrace { get; set; } = false;

    [DisplayName("Trace Raw Input")]
    [Category("99 - Debug")]
    [Description("Writes raw/legacy mouse, cursor warp, device registration, and XInput telemetry. Restart required.")]
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

    // Older builds used raw floating-point implementation units. Nullable
    // aliases preserve them during deserialization and disappear on next save.
    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FreeMouseYawDegreesPerCount { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FreeMousePitchDegreesPerCount { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? YawSpeed { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? PitchSpeed { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MouseHorizontalSensitivity { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MouseVerticalSensitivity { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? GamepadHorizontalSensitivity { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? GamepadVerticalSensitivity { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SplineMouseSensitivityMultiplier { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SplineControllerSensitivityMultiplier { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ControllerDeadzone { get; set; }

    [Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ControllerResponseExponent { get; set; }

    public float GetGamepadCurveExponent() => GamepadResponseCurve switch
    {
        GamepadCurvePreset.Linear => 1.0f,
        GamepadCurvePreset.Responsive => 1.35f,
        GamepadCurvePreset.Calm => 2.0f,
        GamepadCurvePreset.Precision => 2.4f,
        GamepadCurvePreset.Custom => Math.Clamp(CustomGamepadCurveExponent, 1f, 3f),
        _ => 1.7f,
    };

    public void OnDeserialized()
    {
        float? mouseYaw = MouseHorizontalSensitivity ?? FreeMouseYawDegreesPerCount;
        float? mousePitch = MouseVerticalSensitivity ?? FreeMousePitchDegreesPerCount;
        float? gamepadYaw = GamepadHorizontalSensitivity ?? YawSpeed;
        float? gamepadPitch = GamepadVerticalSensitivity ?? PitchSpeed;
        if (mouseYaw is float yawValue)
            MouseHorizontalSensitivityPercent = Math.Clamp((int)MathF.Round(yawValue / 0.04f * 100f), 10, 300);
        if (mousePitch is float pitchValue)
            MouseVerticalSensitivityPercent = Math.Clamp((int)MathF.Round(pitchValue / 0.03f * 100f), 10, 300);
        if (gamepadYaw is float gamepadYawValue)
            GamepadHorizontalSpeed = Math.Clamp((int)MathF.Round(gamepadYawValue), 30, 300);
        if (gamepadPitch is float gamepadPitchValue)
            GamepadVerticalSpeed = Math.Clamp((int)MathF.Round(gamepadPitchValue), 30, 200);
        if (SplineMouseSensitivityMultiplier is float splineMouse)
            SplineMouseSensitivityPercent = Math.Clamp((int)MathF.Round(splineMouse * 100f), 0, 200);
        if (SplineControllerSensitivityMultiplier is float splineGamepad)
            SplineGamepadSensitivityPercent = Math.Clamp((int)MathF.Round(splineGamepad * 100f), 0, 200);
        if (ControllerDeadzone is float deadzone)
            GamepadDeadzonePercent = Math.Clamp((int)MathF.Round(deadzone * 100f), 0, 50);
        if (ControllerResponseExponent is float exponent)
            GamepadResponseCurve = ClosestCurvePreset(exponent);

        FreeMouseYawDegreesPerCount = null;
        FreeMousePitchDegreesPerCount = null;
        YawSpeed = null;
        PitchSpeed = null;
        MouseHorizontalSensitivity = null;
        MouseVerticalSensitivity = null;
        GamepadHorizontalSensitivity = null;
        GamepadVerticalSensitivity = null;
        SplineMouseSensitivityMultiplier = null;
        SplineControllerSensitivityMultiplier = null;
        ControllerDeadzone = null;
        ControllerResponseExponent = null;
    }

    private static GamepadCurvePreset ClosestCurvePreset(float exponent)
    {
        (GamepadCurvePreset Preset, float Exponent)[] presets =
        {
            (GamepadCurvePreset.Linear, 1.0f),
            (GamepadCurvePreset.Responsive, 1.35f),
            (GamepadCurvePreset.Balanced, 1.7f),
            (GamepadCurvePreset.Calm, 2.0f),
            (GamepadCurvePreset.Precision, 2.4f),
        };
        return presets.MinBy(item => Math.Abs(item.Exponent - exponent)).Preset;
    }
}

public enum GamepadCurvePreset
{
    [Display(Name = "Linear")]
    Linear,

    [Display(Name = "Responsive")]
    Responsive,

    [Display(Name = "Balanced (Recommended)")]
    Balanced,

    [Display(Name = "Calm")]
    Calm,

    [Display(Name = "Precision")]
    Precision,

    [Display(Name = "Custom")]
    Custom,
}

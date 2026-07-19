using p3rpc.camfix.Template.Configuration;
using System.ComponentModel;

namespace p3rpc.camfix.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Enable Camera Fix")]
    [Category("General")]
    [Description("Master switch. When enabled, applies smoothing fixes to the overworld camera.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Yaw Speed")]
    [Category("Rotation")]
    [Description("Horizontal rotation speed. Default: 125. Higher = faster.")]
    [DefaultValue(125.0f)]
    public float YawSpeed { get; set; } = 125.0f;

    [DisplayName("Yaw Acceleration")]
    [Category("Rotation")]
    [Description("Time to reach full speed (seconds). Default: 0.1. Set to 0 for instant.")]
    [DefaultValue(0.0f)]
    public float YawAcceleration { get; set; } = 0.0f;

    [DisplayName("Yaw Deceleration")]
    [Category("Rotation")]
    [Description("Time to stop from full speed (seconds). Default: 0.1. Set to 0 for instant.")]
    [DefaultValue(0.0f)]
    public float YawDeceleration { get; set; } = 0.0f;

    [DisplayName("Yaw Press Delay")]
    [Category("Rotation")]
    [Description("Delay before rotation starts (seconds). Default: 0.05. Set to 0 for no delay.")]
    [DefaultValue(0.0f)]
    public float YawPress { get; set; } = 0.0f;

    [DisplayName("Yaw Release Delay")]
    [Category("Rotation")]
    [Description("Delay before deceleration kicks in (seconds). Default: 0.1. Set to 0 for no delay.")]
    [DefaultValue(0.0f)]
    public float YawRelease { get; set; } = 0.0f;

    [DisplayName("Pitch Speed")]
    [Category("Rotation")]
    [Description("Vertical rotation speed. Default: 90. Higher = faster.")]
    [DefaultValue(90.0f)]
    public float PitchSpeed { get; set; } = 90.0f;

    [DisplayName("Pitch Acceleration")]
    [Category("Rotation")]
    [Description("Time to reach full speed (seconds). Default: 0.1. Set to 0 for instant.")]
    [DefaultValue(0.0f)]
    public float PitchAcceleration { get; set; } = 0.0f;

    [DisplayName("Pitch Deceleration")]
    [Category("Rotation")]
    [Description("Time to stop from full speed (seconds). Default: 0.1. Set to 0 for instant.")]
    [DefaultValue(0.0f)]
    public float PitchDeceleration { get; set; } = 0.0f;

    [DisplayName("Pitch Press Delay")]
    [Category("Rotation")]
    [Description("Delay before rotation starts (seconds). Default: 0.0. Set to 0 for no delay.")]
    [DefaultValue(0.0f)]
    public float PitchPress { get; set; } = 0.0f;

    [DisplayName("Pitch Release Delay")]
    [Category("Rotation")]
    [Description("Delay before deceleration kicks in (seconds). Default: 0.1. Set to 0 for no delay.")]
    [DefaultValue(0.0f)]
    public float PitchRelease { get; set; } = 0.0f;

    [DisplayName("Correction Speed")]
    [Category("Correction")]
    [Description("Auto-correction rotation speed. Default: 35.")]
    [DefaultValue(35.0f)]
    public float CorrectionSpeed { get; set; } = 35.0f;

    [DisplayName("Correction Acceleration")]
    [Category("Correction")]
    [Description("Auto-correction accel time. Default: 0.5. Set to 0 for instant. Keep non-zero to ease camera-follow.")]
    [DefaultValue(0.5f)]
    public float CorrectionAcceleration { get; set; } = 0.5f;

    [DisplayName("Correction Deceleration")]
    [Category("Correction")]
    [Description("Auto-correction decel time. Default: 0.3. Set to 0 for instant.")]
    [DefaultValue(0.3f)]
    public float CorrectionDeceleration { get; set; } = 0.3f;

    [DisplayName("Correction Press Delay")]
    [Category("Correction")]
    [Description("Auto-correction press delay. Default: 0.3. Set to 0 for no delay.")]
    [DefaultValue(0.3f)]
    public float CorrectionPress { get; set; } = 0.3f;

    [DisplayName("Correction Release Delay")]
    [Category("Correction")]
    [Description("Auto-correction release delay. Default: 0.0.")]
    [DefaultValue(0.0f)]
    public float CorrectionRelease { get; set; } = 0.0f;

    [DisplayName("Enable Experimental Free Camera Fix")]
    [Category("Experimental - Free Camera")]
    [Description("Uses direct raw mouse displacement and direct XInput for the normal third-person camera while preserving native correction, collision, and pitch limits. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableExperimentalFreeCamera { get; set; } = false;

    [DisplayName("Use Raw Mouse for Free Camera")]
    [Category("Experimental - Free Camera")]
    [Description("Applies physical mouse counts directly as same-frame camera-angle deltas instead of routing them through P3R's center-warped stick-like axis.")]
    [DefaultValue(true)]
    public bool EnableFreeRawMouse { get; set; } = true;

    [DisplayName("Free Camera Mouse Yaw Sensitivity")]
    [Category("Experimental - Free Camera")]
    [Description("Horizontal degrees per physical mouse count. Default: 0.04.")]
    [DefaultValue(0.04f)]
    public float FreeMouseYawDegreesPerCount { get; set; } = 0.04f;

    [DisplayName("Free Camera Mouse Pitch Sensitivity")]
    [Category("Experimental - Free Camera")]
    [Description("Vertical degrees per physical mouse count. Default: 0.03.")]
    [DefaultValue(0.03f)]
    public float FreeMousePitchDegreesPerCount { get; set; } = 0.03f;

    [DisplayName("Invert Free Camera Mouse Y")]
    [Category("Experimental - Free Camera")]
    [Description("Reverses vertical raw-mouse movement in the normal third-person camera.")]
    [DefaultValue(false)]
    public bool InvertFreeMouseY { get; set; } = false;

    [DisplayName("Use Direct XInput for Free Camera")]
    [Category("Experimental - Free Camera")]
    [Description("Reads the right stick before P3R's large upstream deadzone/remap and supplies a direct radial response to the native free-camera velocity path.")]
    [DefaultValue(true)]
    public bool EnableFreeDirectController { get; set; } = true;

    [DisplayName("Free Camera Controller Deadzone")]
    [Category("Experimental - Free Camera")]
    [Description("Radial right-stick deadzone from 0 to 0.95. Default: 0.03.")]
    [DefaultValue(0.03f)]
    public float FreeControllerDeadzone { get; set; } = 0.03f;

    [DisplayName("Free Camera Controller Response Exponent")]
    [Category("Experimental - Free Camera")]
    [Description("Radial response exponent. Values above 1 retain fine low-speed control while full stick still reaches the configured yaw/pitch speeds. Default: 1.7.")]
    [DefaultValue(1.7f)]
    public float FreeControllerResponseExponent { get; set; } = 1.7f;

    [DisplayName("Free Camera Controller Sensitivity")]
    [Category("Experimental - Free Camera")]
    [Description("Multiplier applied after the free-camera deadzone and response curve. Default: 1.0.")]
    [DefaultValue(1.0f)]
    public float FreeControllerSensitivity { get; set; } = 1.0f;

    [DisplayName("Hide Cursor During Native Camera Fades")]
    [Category("Experimental - Camera Cursor")]
    [Description("Suppresses P3R's erroneous Windows arrow for native fade transactions in free and spline gameplay, from fade start until field input ownership resumes. Dialogue and menu cursor ownership remain native. Requires a game restart.")]
    [DefaultValue(true)]
    public bool EnableNativeFadeCursorGuard { get; set; } = true;

    [DisplayName("Native Fade Cursor Timing Bridge")]
    [Category("Experimental - Camera Cursor")]
    [Description("Seconds to bridge the measured operation-sampling gaps immediately before and after P3R's native fade mode in free and spline cameras. Cursor-only; does not change camera input or ownership. Default: 0.075.")]
    [DefaultValue(0.075f)]
    public float NativeFadeCursorBridgeSeconds { get; set; } = 0.075f;

    [DisplayName("Enable Experimental Free Camera Trace")]
    [Category("Diagnostics")]
    [Description("Logs free-camera source selection, direct inputs, raw timing, and resulting component angles. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableExperimentalFreeCameraTrace { get; set; } = false;

    [DisplayName("Enable Experimental Spline Camera Fix")]
    [Category("Experimental - Spline Camera")]
    [Description("Experimental vNext path for spline/rail cameras. Removes the native 166.7 ms interpolator and its input thresholds. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableExperimentalSplineCamera { get; set; } = false;

    [DisplayName("Use Raw Mouse for Spline Cameras")]
    [Category("Experimental - Spline Camera")]
    [Description("Accumulates physical raw mouse counts into a persistent spline-camera angle instead of treating the mouse like a recentering stick.")]
    [DefaultValue(true)]
    public bool EnableSplineRawMouse { get; set; } = true;

    [DisplayName("Spline Mouse Yaw Sensitivity")]
    [Category("Experimental - Spline Camera")]
    [Description("Horizontal degrees per physical mouse count. Default: 0.04.")]
    [DefaultValue(0.04f)]
    public float SplineMouseYawDegreesPerCount { get; set; } = 0.04f;

    [DisplayName("Spline Mouse Pitch Sensitivity")]
    [Category("Experimental - Spline Camera")]
    [Description("Vertical degrees per physical mouse count. Default: 0.03.")]
    [DefaultValue(0.03f)]
    public float SplineMousePitchDegreesPerCount { get; set; } = 0.03f;

    [DisplayName("Invert Spline Mouse Y")]
    [Category("Experimental - Spline Camera")]
    [Description("Reverses vertical raw-mouse movement in spline/rail cameras.")]
    [DefaultValue(false)]
    public bool InvertSplineMouseY { get; set; } = false;

    [DisplayName("Use Direct XInput for Spline Cameras")]
    [Category("Experimental - Spline Camera")]
    [Description("Reads the right stick before the game's large upstream deadzone/remap and applies the configurable response below.")]
    [DefaultValue(true)]
    public bool EnableSplineDirectController { get; set; } = true;

    [DisplayName("Spline Controller Deadzone")]
    [Category("Experimental - Spline Camera")]
    [Description("Radial right-stick deadzone from 0 to 0.95. Default: 0.03.")]
    [DefaultValue(0.03f)]
    public float SplineControllerDeadzone { get; set; } = 0.03f;

    [DisplayName("Spline Controller Response Exponent")]
    [Category("Experimental - Spline Camera")]
    [Description("Response curve exponent. 1 is linear; values above 1 give finer control near center while retaining full range. Default: 1.7.")]
    [DefaultValue(1.7f)]
    public float SplineControllerResponseExponent { get; set; } = 1.7f;

    [DisplayName("Spline Controller Sensitivity")]
    [Category("Experimental - Spline Camera")]
    [Description("Multiplier applied after the deadzone and response curve. Default: 1.0.")]
    [DefaultValue(1.0f)]
    public float SplineControllerSensitivity { get; set; } = 1.0f;

    [DisplayName("Spline Controller Small-Movement Smoothing")]
    [Category("Experimental - Spline Camera")]
    [Description("Exponential time constant in seconds for small stick fluctuations. Default: 0.11. Set to 0 for direct response.")]
    [DefaultValue(0.11f)]
    public float SplineControllerSmallSmoothing { get; set; } = 0.11f;

    [DisplayName("Spline Controller Large-Movement Smoothing")]
    [Category("Experimental - Spline Camera")]
    [Description("Exponential time constant in seconds for large deliberate stick changes. Default: 0.04.")]
    [DefaultValue(0.04f)]
    public float SplineControllerLargeSmoothing { get; set; } = 0.04f;

    [DisplayName("Spline Controller Recenter Smoothing")]
    [Category("Experimental - Spline Camera")]
    [Description("Exponential time constant in seconds when the stick returns to center. Default: 0.075.")]
    [DefaultValue(0.075f)]
    public float SplineControllerRecenterSmoothing { get; set; } = 0.075f;

    [DisplayName("Spline Controller Large-Change Threshold")]
    [Category("Experimental - Spline Camera")]
    [Description("Normalized target-distance at which response approaches the fast large-movement smoothing value. Default: 0.35.")]
    [DefaultValue(0.35f)]
    public float SplineControllerLargeChangeThreshold { get; set; } = 0.35f;

    [DisplayName("Spline Controller Target Hysteresis")]
    [Category("Experimental - Spline Camera")]
    [Description("Ignores target changes smaller than this normalized distance to suppress hand jitter. Default: 0.01. Set to 0 to disable.")]
    [DefaultValue(0.01f)]
    public float SplineControllerTargetHysteresis { get; set; } = 0.01f;

    [DisplayName("Spline Mouse Smoothing")]
    [Category("Experimental - Spline Camera")]
    [Description("Optional exponential time constant in seconds for visible mouse motion. Default: 0 (direct raw movement). Nonzero values add intentional inertia.")]
    [DefaultValue(0.0f)]
    public float SplineMouseSmoothing { get; set; } = 0.0f;

    [DisplayName("Recenter Spline Camera During Native Input Locks")]
    [Category("Experimental - Spline Camera")]
    [Description("Returns the spline offset to its authored center when P3R disables field-camera input for dialogue, rail transitions, or menus. Uses the smooth autonomous duration below instead of P3R's rigid 20-degrees-per-second follower.")]
    [DefaultValue(true)]
    public bool EnableSplineNativeLockRecenter { get; set; } = true;

    [DisplayName("Spline Autonomous Recenter Duration")]
    [Category("Experimental - Spline Camera")]
    [Description("Seconds for a coordinated smooth return from the current offset to center. This affects only autonomous recentering, never physical mouse response. Default: 0.5.")]
    [DefaultValue(0.5f)]
    public float SplineAutonomousRecenterDuration { get; set; } = 0.5f;

    [DisplayName("Enable Moving-Idle Mouse Recenter")]
    [Category("Experimental - Spline Camera")]
    [Description("After the mouse is idle, returns toward center only when the authored rail camera is moving. A stationary deliberate glance remains held.")]
    [DefaultValue(true)]
    public bool EnableSplineMouseIdleRecenter { get; set; } = true;

    [DisplayName("Spline Mouse Idle Recenter Delay")]
    [Category("Experimental - Spline Camera")]
    [Description("Seconds without accepted mouse movement before rail motion may trigger autonomous recentering. Default: 2.5.")]
    [DefaultValue(2.5f)]
    public float SplineMouseIdleRecenterDelay { get; set; } = 2.5f;

    [DisplayName("Reject Cursor-Warp Mouse Packets")]
    [Category("Experimental - Spline Camera")]
    [Description("Rejects only raw mouse packets that match a recent large SetCursorPos warp. Genuine unmatched raw movement is unchanged.")]
    [DefaultValue(true)]
    public bool EnableSplineCursorWarpRejection { get; set; } = true;

    [DisplayName("Cursor-Warp Detection Threshold")]
    [Category("Experimental - Spline Camera")]
    [Description("Minimum cursor-warp delta in counts before it can be matched against raw input. Default: 128.")]
    [DefaultValue(128)]
    public int SplineCursorWarpMinimumCounts { get; set; } = 128;

    [DisplayName("Cursor-Warp Match Tolerance")]
    [Category("Experimental - Spline Camera")]
    [Description("Per-axis count tolerance when matching a raw packet to a recent cursor warp. Default: 8.")]
    [DefaultValue(8)]
    public int SplineCursorWarpMatchTolerance { get; set; } = 8;

    [DisplayName("Hide Erroneous Spline Gameplay Cursor")]
    [Category("Experimental - Spline Camera")]
    [Description("Suppresses non-null Windows cursor selections only while the spline camera is actively advancing. Paused/menu frames remain cursor-enabled. Does not alter the global ShowCursor count.")]
    [DefaultValue(true)]
    public bool EnableSplineGameplayCursorGuard { get; set; } = true;

    [DisplayName("Enable Legacy Mouse Fallback")]
    [Category("Experimental - Spline Camera")]
    [Description("When WM_INPUT is unavailable, integrates the game's legacy mouse axis as an FPS-independent angular velocity instead of disabling the mouse.")]
    [DefaultValue(true)]
    public bool EnableSplineLegacyMouseFallback { get; set; } = true;

    [DisplayName("Legacy Mouse Yaw Speed")]
    [Category("Experimental - Spline Camera")]
    [Description("Degrees per second at a legacy mouse-axis value of 1. Used only when raw counts are unavailable. Default: 150.")]
    [DefaultValue(150.0f)]
    public float SplineLegacyMouseYawSpeed { get; set; } = 150.0f;

    [DisplayName("Legacy Mouse Pitch Speed")]
    [Category("Experimental - Spline Camera")]
    [Description("Degrees per second at a legacy mouse-axis value of 1. Used only when raw counts are unavailable. Default: 90.")]
    [DefaultValue(90.0f)]
    public float SplineLegacyMousePitchSpeed { get; set; } = 90.0f;

    [DisplayName("Enable Experimental Spline Trace")]
    [Category("Diagnostics")]
    [Description("Logs vNext raw counts, device ownership, targets, filtered output, and final degree offsets for diagnosis. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableExperimentalSplineTrace { get; set; } = false;

    [DisplayName("Enable Camera Transition Trace")]
    [Category("Diagnostics")]
    [Description("Research mode. Continuously logs camera ownership, Windows cursor calls, and the native UI fade-player state across free, spline, loading, and camera-handoff periods. Read-only; requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableCameraTransitionTrace { get; set; } = false;

    [DisplayName("Enable Camera Filter Trace")]
    [Category("Diagnostics")]
    [Description("Research mode. Hooks the native camera-axis filter and writes a timestamped CSV trace. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableCameraFilterTrace { get; set; } = false;

    [DisplayName("Maximum Trace Samples")]
    [Category("Diagnostics")]
    [Description("Maximum number of native filter calls captured in one run. Default 262144 (about 18 minutes at 120 FPS with two calls per frame).")]
    [DefaultValue(262144)]
    public int CameraFilterTraceCapacity { get; set; } = 262144;

    [DisplayName("Enable Raw Input Trace")]
    [Category("Diagnostics")]
    [Description("Research mode. Logs legacy/raw mouse messages, raw-input reads, cursor sampling/warping, device registration, and XInput on the camera trace's QPC clock. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableRawInputTrace { get; set; } = false;

    [DisplayName("Enable Fixed Camera Trace")]
    [Category("Diagnostics")]
    [Description("Research mode. Logs literal-fixed and spline/rail camera paths, including the owning per-frame camera-operation tick. Requires a game restart.")]
    [DefaultValue(false)]
    public bool EnableFixedCameraTrace { get; set; } = false;
}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}

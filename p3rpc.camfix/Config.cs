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
}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}

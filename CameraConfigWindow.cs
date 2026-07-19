using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using p3rpc.camfix.Template.Configuration;

namespace p3rpc.camfix.Configuration;

internal sealed class CameraConfigWindow : Window
{
    private readonly Config _source;
    private Config _working;
    private readonly ContentControl _contentHost = new();
    private CurvePreview? _curvePreview;
    private TextBlock? _curveSummary;
    private FrameworkElement? _customCurveEditor;

    private CameraConfigWindow(Config source)
    {
        _source = source;
        _working = Clone(source);

        Title = "Persona 3 Reload Camera Fix";
        Width = 920;
        Height = 720;
        MinWidth = 760;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        FontFamily = new FontFamily("Segoe UI");
        ApplyReloadedWindowTheme();
        SetResourceReference(BackgroundProperty, "BackgroundColorBrush");
        SetResourceReference(ForegroundProperty, "TextColorBrush");

        Content = BuildWindow();
    }

    private void ApplyReloadedWindowTheme()
    {
        // The configurator is hosted inside Reloaded-II, so use its live
        // resources and window view-model without introducing a runtime
        // dependency into the game-side mod assembly.
        Style? reloadedStyle = TryFindResource("ReloadedWindow") as Style;
        Type? viewModelType = Type.GetType(
            "Reloaded.WPF.Theme.Default.WindowViewModel, Reloaded.WPF.Theme.Default",
            throwOnError: false);
        if (reloadedStyle == null || viewModelType == null)
            return;

        try
        {
            DataContext = Activator.CreateInstance(viewModelType, this);
            Style = reloadedStyle;
        }
        catch
        {
            // A future Reloaded version may change the theme view-model API.
            // In that case the ordinary WPF frame remains functional while
            // the content still uses Reloaded's dynamic palette resources.
        }
    }

    public static bool Show(Config configuration)
    {
        bool? result = false;
        void Open()
        {
            var window = new CameraConfigWindow(configuration);
            Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive)
                            ?? Application.Current?.MainWindow;
            if (owner != null && owner != window)
                window.Owner = owner;
            result = window.ShowDialog();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Open);
        else
            Open();
        return result == true;
    }

    private UIElement BuildWindow()
    {
        var root = new DockPanel { LastChildFill = true };
        root.SetResourceReference(Panel.BackgroundProperty, "BackgroundColorBrush");
        root.Children.Add(BuildButtons());
        _contentHost.Content = BuildTabs();
        root.Children.Add(_contentHost);
        return root;
    }

    private UIElement BuildButtons()
    {
        var panel = new DockPanel
        {
            Margin = new Thickness(14, 8, 14, 12),
            LastChildFill = false,
        };
        DockPanel.SetDock(panel, Dock.Bottom);

        var reset = MakeButton("Reset Defaults", (_, _) =>
        {
            _working = new Config();
            _contentHost.Content = BuildTabs();
        });
        DockPanel.SetDock(reset, Dock.Left);
        panel.Children.Add(reset);

        var cancel = MakeButton("Cancel", (_, _) => DialogResult = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(cancel, Dock.Right);
        panel.Children.Add(cancel);

        var save = MakeButton("Save", (_, _) => SaveAndClose());
        save.Margin = new Thickness(8, 0, 0, 0);
        save.MinWidth = 96;
        save.FontWeight = FontWeights.SemiBold;
        save.SetResourceReference(BackgroundProperty, "AccentColorLightBrush");
        save.SetResourceReference(ForegroundProperty, "TextColorBrush");
        save.IsDefault = true;
        DockPanel.SetDock(save, Dock.Right);
        panel.Children.Add(save);
        return panel;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Margin = new Thickness(14, 12, 14, 0) };
        tabs.SetResourceReference(BackgroundProperty, "BackgroundColorBrush");
        tabs.SetResourceReference(ForegroundProperty, "TextColorBrush");
        tabs.Items.Add(new TabItem { Header = "Camera", Content = BuildBasicTab() });
        tabs.Items.Add(new TabItem { Header = "Advanced", Content = BuildAdvancedTab() });
        tabs.Items.Add(new TabItem { Header = "Debug", Content = BuildDebugTab() });
        return tabs;
    }

    private UIElement BuildBasicTab()
    {
        var grid = new Grid { Margin = new Thickness(10) };
        grid.SetResourceReference(Panel.BackgroundProperty, "BackgroundColorBrush");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var settings = new StackPanel();
        settings.Children.Add(MakeCheckBox("Enable Camera Fix", "Master switch; restart after changing.", () => _working.Enabled, value => _working.Enabled = value));

        settings.Children.Add(MakeHeading("Mouse"));
        settings.Children.Add(MakeIntSlider("Horizontal Sensitivity", "Free-camera mouse base.", 10, 300, 1,
            () => _working.MouseHorizontalSensitivityPercent, value => _working.MouseHorizontalSensitivityPercent = value, value => $"{value}%"));
        settings.Children.Add(MakeIntSlider("Vertical Sensitivity", "Free-camera mouse base.", 10, 300, 1,
            () => _working.MouseVerticalSensitivityPercent, value => _working.MouseVerticalSensitivityPercent = value, value => $"{value}%"));
        settings.Children.Add(MakeCheckBox("Invert Mouse Y", "Applies to free and spline cameras.", () => _working.InvertMouseY, value => _working.InvertMouseY = value));

        settings.Children.Add(MakeHeading("Gamepad"));
        settings.Children.Add(MakeIntSlider("Horizontal Sensitivity", "Maximum free-camera turn speed.", 30, 300, 5,
            () => _working.GamepadHorizontalSpeed, value => { _working.GamepadHorizontalSpeed = value; RefreshCurve(); }, value => $"{value}°/s"));
        settings.Children.Add(MakeIntSlider("Vertical Sensitivity", "Maximum free-camera turn speed.", 30, 200, 5,
            () => _working.GamepadVerticalSpeed, value => _working.GamepadVerticalSpeed = value, value => $"{value}°/s"));
        settings.Children.Add(MakeIntSlider("Deadzone", "Radial deadzone applied before the curve.", 0, 50, 1,
            () => _working.GamepadDeadzonePercent, value => { _working.GamepadDeadzonePercent = value; RefreshCurve(); }, value => $"{value}%"));
        settings.Children.Add(MakeCurveSelector());

        settings.Children.Add(MakeHeading("Spline / Rail Camera"));
        settings.Children.Add(MakeIntSlider("Mouse Sensitivity Multiplier", "Relative to the free-camera mouse base.", 0, 200, 1,
            () => _working.SplineMouseSensitivityPercent, value => _working.SplineMouseSensitivityPercent = value, value => $"{value}%"));
        settings.Children.Add(MakeIntSlider("Gamepad Sensitivity Multiplier", "Relative to the free-camera gamepad base.", 0, 200, 1,
            () => _working.SplineGamepadSensitivityPercent, value => _working.SplineGamepadSensitivityPercent = value, value => $"{value}%"));

        var scroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BackgroundProperty, "BackgroundColorBrush");
        Grid.SetColumn(scroll, 0);
        grid.Children.Add(scroll);

        var previewPanel = new DockPanel();
        previewPanel.SetResourceReference(Panel.BackgroundProperty, "BackgroundColorBrush");
        var title = new TextBlock
        {
            Text = "Gamepad Response",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 8),
        };
        DockPanel.SetDock(title, Dock.Top);
        previewPanel.Children.Add(title);
        _curveSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _curveSummary.SetResourceReference(ForegroundProperty, "BorderColorLightBrush");
        DockPanel.SetDock(_curveSummary, Dock.Bottom);
        previewPanel.Children.Add(_curveSummary);
        _curvePreview = new CurvePreview { MinHeight = 330 };
        previewPanel.Children.Add(_curvePreview);
        Grid.SetColumn(previewPanel, 2);
        grid.Children.Add(previewPanel);
        RefreshCurve();
        return grid;
    }

    private UIElement MakeCurveSelector()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 8) };
        panel.Children.Add(MakeLabel("Response Curve", "All presets are ordinary power curves and reach 100% at full stick."));
        var combo = new ComboBox
        {
            ItemsSource = Enum.GetValues<GamepadCurvePreset>(),
            SelectedItem = _working.GamepadResponseCurve,
            Margin = new Thickness(0, 3, 0, 4),
        };
        combo.ItemTemplate = MakeEnumTemplate();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is GamepadCurvePreset preset)
            {
                _working.GamepadResponseCurve = preset;
                if (_customCurveEditor != null)
                    _customCurveEditor.Visibility = preset == GamepadCurvePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
                RefreshCurve();
            }
        };
        panel.Children.Add(combo);
        _customCurveEditor = MakeFloatSlider("Custom Exponent", "1.00 is linear; higher is calmer near center.", 1f, 3f, 0.05f,
            () => _working.CustomGamepadCurveExponent,
            value => { _working.CustomGamepadCurveExponent = value; RefreshCurve(); },
            value => value.ToString("0.00", CultureInfo.InvariantCulture));
        _customCurveEditor.Visibility = _working.GamepadResponseCurve == GamepadCurvePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(_customCurveEditor);
        return panel;
    }

    private UIElement BuildAdvancedTab()
    {
        var root = new StackPanel { Margin = new Thickness(10) };

        var input = MakeGroup("Input Sources");
        input.Children.Add(MakeCheckBox("Enable Free-Camera Replacement", "Direct input for the normal third-person camera; restart required.", () => _working.EnableFreeCameraFix, value => _working.EnableFreeCameraFix = value));
        input.Children.Add(MakeCheckBox("Enable Spline-Camera Replacement", "Direct input for spline/rail cameras; restart required.", () => _working.EnableSplineCameraFix, value => _working.EnableSplineCameraFix = value));
        input.Children.Add(MakeCheckBox("Use Raw Mouse Input", "Bypasses P3R's center-warped mouse path.", () => _working.EnableRawMouse, value => _working.EnableRawMouse = value));
        input.Children.Add(MakeCheckBox("Use Direct Gamepad Input", "Bypasses P3R's upstream deadzone/remap.", () => _working.EnableDirectController, value => _working.EnableDirectController = value));
        root.Children.Add(WrapGroup("Input Sources", input));

        var spline = MakeGroup("Spline Camera");
        spline.Children.Add(MakeFloatBox("Small-Movement Smoothing", "Seconds; 0 is direct.", () => _working.SplineControllerSmallSmoothing, value => _working.SplineControllerSmallSmoothing = value, 0f, 1f, "0.000"));
        spline.Children.Add(MakeFloatBox("Large-Movement Smoothing", "Seconds.", () => _working.SplineControllerLargeSmoothing, value => _working.SplineControllerLargeSmoothing = value, 0f, 1f, "0.000"));
        spline.Children.Add(MakeFloatBox("Stick-Release Smoothing", "Seconds.", () => _working.SplineControllerRecenterSmoothing, value => _working.SplineControllerRecenterSmoothing = value, 0f, 1f, "0.000"));
        spline.Children.Add(MakeFloatBox("Large-Change Threshold", "Normalized target distance.", () => _working.SplineControllerLargeChangeThreshold, value => _working.SplineControllerLargeChangeThreshold = value, 0.001f, 1f, "0.000"));
        spline.Children.Add(MakeFloatBox("Stick Hysteresis", "Normalized target distance; 0 disables.", () => _working.SplineControllerTargetHysteresis, value => _working.SplineControllerTargetHysteresis = value, 0f, 0.25f, "0.000"));
        spline.Children.Add(MakeFloatBox("Mouse Smoothing", "Seconds; 0 is recommended and direct.", () => _working.SplineMouseSmoothing, value => _working.SplineMouseSmoothing = value, 0f, 1f, "0.000"));
        spline.Children.Add(MakeCheckBox("Recenter During Native Input Locks", "Restores authored framing during dialogue, menus, and transitions.", () => _working.EnableSplineNativeLockRecenter, value => _working.EnableSplineNativeLockRecenter = value));
        spline.Children.Add(MakeFloatBox("Autonomous Recenter Duration", "Seconds.", () => _working.SplineAutonomousRecenterDuration, value => _working.SplineAutonomousRecenterDuration = value, 0.05f, 2f, "0.00"));
        spline.Children.Add(MakeCheckBox("Recenter Idle Mouse During Rail Motion", "Stationary glances remain held.", () => _working.EnableSplineMouseIdleRecenter, value => _working.EnableSplineMouseIdleRecenter = value));
        spline.Children.Add(MakeFloatBox("Mouse Idle Delay", "Seconds before moving-rail recenter.", () => _working.SplineMouseIdleRecenterDelay, value => _working.SplineMouseIdleRecenterDelay = value, 0f, 30f, "0.0"));
        root.Children.Add(WrapGroup("Spline Camera", spline));

        var native = MakeGroup("Native Free Camera");
        AddNativeFields(native);
        root.Children.Add(WrapGroup("Native Free Camera", native));

        var compatibility = MakeGroup("Compatibility and Cursor Ownership");
        compatibility.Children.Add(MakeCheckBox("Reject Game Cursor-Warp Packets", "Rejects only raw packets matching a game-driven cursor warp.", () => _working.EnableSplineCursorWarpRejection, value => _working.EnableSplineCursorWarpRejection = value));
        compatibility.Children.Add(MakeIntBox("Cursor-Warp Detection Threshold", "Counts.", () => _working.SplineCursorWarpMinimumCounts, value => _working.SplineCursorWarpMinimumCounts = value, 64, 8192));
        compatibility.Children.Add(MakeIntBox("Cursor-Warp Match Tolerance", "Counts per axis.", () => _working.SplineCursorWarpMatchTolerance, value => _working.SplineCursorWarpMatchTolerance = value, 0, 64));
        compatibility.Children.Add(MakeCheckBox("Hide Erroneous Gameplay Cursor", "Retains native dialogue and UI ownership.", () => _working.EnableSplineGameplayCursorGuard, value => _working.EnableSplineGameplayCursorGuard = value));
        compatibility.Children.Add(MakeCheckBox("Hide Cursor During Native Fades", "Uses native fade/UI ownership; restart required.", () => _working.EnableNativeFadeCursorGuard, value => _working.EnableNativeFadeCursorGuard = value));
        compatibility.Children.Add(MakeFloatBox("Fade Boundary Bridge", "Seconds.", () => _working.NativeFadeCursorBridgeSeconds, value => _working.NativeFadeCursorBridgeSeconds = value, 0f, 0.25f, "0.000"));
        compatibility.Children.Add(MakeCheckBox("Enable Legacy Mouse Fallback", "Temporary fallback when raw input is unavailable.", () => _working.EnableSplineLegacyMouseFallback, value => _working.EnableSplineLegacyMouseFallback = value));
        compatibility.Children.Add(MakeFloatBox("Legacy Mouse Horizontal Speed", "Degrees/second.", () => _working.SplineLegacyMouseYawSpeed, value => _working.SplineLegacyMouseYawSpeed = value, 0f, 2000f, "0"));
        compatibility.Children.Add(MakeFloatBox("Legacy Mouse Vertical Speed", "Degrees/second.", () => _working.SplineLegacyMousePitchSpeed, value => _working.SplineLegacyMousePitchSpeed = value, 0f, 2000f, "0"));
        root.Children.Add(WrapGroup("Compatibility and Cursor Ownership", compatibility));

        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BackgroundProperty, "BackgroundColorBrush");
        return scroll;
    }

    private UIElement BuildDebugTab()
    {
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = "Leave these disabled for normal play. Traces allocate large buffers and write high-volume CSV files.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        ((TextBlock)root.Children[^1]).SetResourceReference(ForegroundProperty, "BorderColorLightBrush");
        root.Children.Add(MakeCheckBox("Trace Free Camera", "Restart required.", () => _working.EnableFreeCameraTrace, value => _working.EnableFreeCameraTrace = value));
        root.Children.Add(MakeCheckBox("Trace Spline Camera", "Restart required.", () => _working.EnableSplineCameraTrace, value => _working.EnableSplineCameraTrace = value));
        root.Children.Add(MakeCheckBox("Trace Camera Transitions and UI", "Restart required.", () => _working.EnableCameraTransitionTrace, value => _working.EnableCameraTransitionTrace = value));
        root.Children.Add(MakeCheckBox("Trace Native Camera Filter", "Restart required.", () => _working.EnableNativeCameraFilterTrace, value => _working.EnableNativeCameraFilterTrace = value));
        root.Children.Add(MakeCheckBox("Trace Raw Input", "Restart required.", () => _working.EnableRawInputTrace, value => _working.EnableRawInputTrace = value));
        root.Children.Add(MakeCheckBox("Trace Literal Fixed Cameras", "This does not mean spline cameras; restart required.", () => _working.EnableLiteralFixedCameraTrace, value => _working.EnableLiteralFixedCameraTrace = value));
        root.Children.Add(MakeIntBox("Maximum Trace Samples", "Per enabled trace.", () => _working.TraceCapacity, value => _working.TraceCapacity = value, 1024, 2_000_000));
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BackgroundProperty, "BackgroundColorBrush");
        return scroll;
    }

    private void AddNativeFields(Panel panel)
    {
        panel.Children.Add(MakeFloatBox("Yaw Acceleration", "Seconds.", () => _working.YawAcceleration, value => _working.YawAcceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Yaw Deceleration", "Seconds.", () => _working.YawDeceleration, value => _working.YawDeceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Yaw Press Delay", "Seconds.", () => _working.YawPress, value => _working.YawPress = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Yaw Release Delay", "Seconds.", () => _working.YawRelease, value => _working.YawRelease = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Pitch Acceleration", "Seconds.", () => _working.PitchAcceleration, value => _working.PitchAcceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Pitch Deceleration", "Seconds.", () => _working.PitchDeceleration, value => _working.PitchDeceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Pitch Press Delay", "Seconds.", () => _working.PitchPress, value => _working.PitchPress = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Pitch Release Delay", "Seconds.", () => _working.PitchRelease, value => _working.PitchRelease = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Auto-Correction Speed", "Degrees/second.", () => _working.CorrectionSpeed, value => _working.CorrectionSpeed = value, 0f, 1000f, "0.0"));
        panel.Children.Add(MakeFloatBox("Correction Acceleration", "Seconds.", () => _working.CorrectionAcceleration, value => _working.CorrectionAcceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Correction Deceleration", "Seconds.", () => _working.CorrectionDeceleration, value => _working.CorrectionDeceleration = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Correction Press Delay", "Seconds.", () => _working.CorrectionPress, value => _working.CorrectionPress = value, 0f, 5f, "0.000"));
        panel.Children.Add(MakeFloatBox("Correction Release Delay", "Seconds.", () => _working.CorrectionRelease, value => _working.CorrectionRelease = value, 0f, 5f, "0.000"));
    }

    private static StackPanel MakeGroup(string _) => new() { Margin = new Thickness(0, 0, 0, 4) };

    private static FrameworkElement WrapGroup(string title, UIElement content)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var heading = MakeHeading(title);
        heading.Margin = new Thickness(0, 5, 0, 6);
        section.Children.Add(heading);

        var divider = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 7) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderColorDarkBrush");
        section.Children.Add(divider);

        var body = new Border
        {
            Child = content,
            Padding = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
        };
        section.Children.Add(body);
        return section;
    }

    private static TextBlock MakeHeading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 5),
    };

    private static FrameworkElement MakeCheckBox(string label, string description, Func<bool> get, Action<bool> set)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 5, 0, 5),
            ToolTip = description,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock
        {
            Text = description,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 0),
        };
        detail.SetResourceReference(ForegroundProperty, "BorderColorLightBrush");
        copy.Children.Add(detail);
        row.Children.Add(copy);

        var toggle = new CameraToggleSwitch
        {
            IsChecked = get(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 2, 0),
            ToolTip = description,
        };
        AutomationProperties.SetName(toggle, label);
        toggle.Checked += (_, _) => set(true);
        toggle.Unchecked += (_, _) => set(false);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static FrameworkElement MakeIntSlider(string label, string description, int min, int max, int tick,
        Func<int> get, Action<int> set, Func<int, string> format)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 7), ToolTip = description };
        var header = MakeSliderHeader(label, format(get()));
        var valueText = (TextBlock)header.Children[1];
        panel.Children.Add(header);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            SmallChange = tick,
            LargeChange = tick * 5,
            IsSnapToTickEnabled = true,
            Value = get(),
        };
        slider.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(slider.Value);
            set(value);
            valueText.Text = format(value);
        };
        panel.Children.Add(slider);
        return panel;
    }

    private static FrameworkElement MakeFloatSlider(string label, string description, float min, float max, float tick,
        Func<float> get, Action<float> set, Func<float, string> format)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 7), ToolTip = description };
        var header = MakeSliderHeader(label, format(get()));
        var valueText = (TextBlock)header.Children[1];
        panel.Children.Add(header);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            SmallChange = tick,
            LargeChange = tick * 5,
            IsSnapToTickEnabled = true,
            Value = get(),
        };
        slider.ValueChanged += (_, _) =>
        {
            float value = (float)Math.Round(slider.Value / tick) * tick;
            set(value);
            valueText.Text = format(value);
        };
        panel.Children.Add(slider);
        return panel;
    }

    private static Grid MakeSliderHeader(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label });
        var valueText = new TextBlock { Text = value, Margin = new Thickness(8, 0, 0, 0) };
        valueText.SetResourceReference(ForegroundProperty, "BorderColorLightBrush");
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private static FrameworkElement MakeFloatBox(string label, string description, Func<float> get, Action<float> set,
        float min, float max, string format) => MakeNumericBox(label, description, get().ToString(format, CultureInfo.InvariantCulture), text =>
    {
        if (!TryParseFloat(text, out float value)) return false;
        set(Math.Clamp(value, min, max));
        return true;
    }, () => get().ToString(format, CultureInfo.InvariantCulture));

    private static FrameworkElement MakeIntBox(string label, string description, Func<int> get, Action<int> set,
        int min, int max) => MakeNumericBox(label, description, get().ToString(CultureInfo.InvariantCulture), text =>
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)) return false;
        set(Math.Clamp(value, min, max));
        return true;
    }, () => get().ToString(CultureInfo.InvariantCulture));

    private static FrameworkElement MakeNumericBox(string label, string description, string initial,
        Func<string, bool> apply, Func<string> canonical)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3), ToolTip = description };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = initial, HorizontalContentAlignment = HorizontalAlignment.Right, Padding = new Thickness(4, 2, 4, 2) };
        Grid.SetColumn(box, 1);
        void Commit()
        {
            if (!apply(box.Text))
                box.BorderBrush = Brushes.OrangeRed;
            else
            {
                box.Text = canonical();
                box.ClearValue(Border.BorderBrushProperty);
            }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
            }
        };
        grid.Children.Add(box);
        return grid;
    }

    private static FrameworkElement MakeLabel(string label, string description) => new TextBlock
    {
        Text = label,
        ToolTip = description,
    };

    private static DataTemplate MakeEnumTemplate()
    {
        var template = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new CurveNameConverter() });
        template.VisualTree = factory;
        return template;
    }

    private static Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), MinWidth = 82 };
        button.Click += onClick;
        return button;
    }

    private void RefreshCurve()
    {
        float exponent = _working.GetGamepadCurveExponent();
        _curvePreview?.SetCurve(_working.GamepadDeadzonePercent / 100f, exponent);
        if (_curveSummary != null)
        {
            string name = CurveNameConverter.GetName(_working.GamepadResponseCurve);
            _curveSummary.Text = $"{name} · exponent {exponent:0.00}\n" +
                                 $"{_working.GamepadDeadzonePercent}% deadzone · full stick = {_working.GamepadHorizontalSpeed}°/s horizontal";
        }
    }

    private void SaveAndClose()
    {
        foreach (PropertyInfo property in typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.SetMethod?.IsPublic != true)
                continue;
            if (property.GetCustomAttribute<BrowsableAttribute>()?.Browsable == false)
                continue;
            property.SetValue(_source, property.GetValue(_working));
        }
        _source.Save?.Invoke();
        DialogResult = true;
    }

    private static Config Clone(Config source)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(source, Config.SerializerOptions);
        return JsonSerializer.Deserialize<Config>(json, Config.SerializerOptions) ?? new Config();
    }

    private static bool TryParseFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private sealed class CurveNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is GamepadCurvePreset preset ? GetName(preset) : value?.ToString() ?? string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        public static string GetName(GamepadCurvePreset preset) => preset switch
        {
            GamepadCurvePreset.Balanced => "Balanced (Recommended)",
            _ => preset.ToString(),
        };
    }
}

internal sealed class CameraToggleSwitch : ToggleButton
{
    private static readonly Brush FallbackOn = new SolidColorBrush(Color.FromRgb(182, 99, 99));
    private static readonly Brush FallbackOff = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static readonly Brush FallbackThumb = Brushes.White;

    public CameraToggleSwitch()
    {
        Width = 46;
        Height = 26;
        Cursor = Cursors.Hand;
        Focusable = true;
        OverridesDefaultStyle = true;
        SetResourceReference(BackgroundProperty, "AccentColorLighterBrush");
        SetResourceReference(BorderBrushProperty, "BorderColorBrush");
        SetResourceReference(ForegroundProperty, "TextColorBrush");
        Checked += (_, _) => InvalidateVisual();
        Unchecked += (_, _) => InvalidateVisual();
        IsEnabledChanged += (_, _) => InvalidateVisual();
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double diameter = Math.Max(10, ActualHeight - 6);
        double thumbX = IsChecked == true ? ActualWidth - diameter - 3 : 3;
        Brush track = IsChecked == true ? Background ?? FallbackOn : BorderBrush ?? FallbackOff;
        Brush thumb = Foreground ?? FallbackThumb;

        double opacity = IsEnabled ? (IsMouseOver ? 1.0 : 0.88) : 0.4;
        drawingContext.PushOpacity(opacity);
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, ActualWidth, ActualHeight), ActualHeight / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(thumb, null,
            new Point(thumbX + (diameter / 2), ActualHeight / 2), diameter / 2, diameter / 2);
        drawingContext.Pop();

        if (IsKeyboardFocused)
        {
            var focusPen = new Pen(thumb, 1) { DashStyle = DashStyles.Dot };
            drawingContext.DrawRoundedRectangle(null, focusPen,
                new Rect(1.5, 1.5, Math.Max(0, ActualWidth - 3), Math.Max(0, ActualHeight - 3)),
                ActualHeight / 2, ActualHeight / 2);
        }
    }
}

internal sealed class CurvePreview : FrameworkElement
{
    private float _deadzone = 0.03f;
    private float _exponent = 1.7f;

    public void SetCurve(float deadzone, float exponent)
    {
        _deadzone = Math.Clamp(deadzone, 0f, 0.5f);
        _exponent = Math.Clamp(exponent, 1f, 3f);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = Math.Max(ActualWidth, 240);
        double height = Math.Max(ActualHeight, 240);
        var bounds = new Rect(0, 0, width, height);
        Brush surfaceBrush = ThemeBrush("BackgroundColorBrush", Color.FromRgb(29, 33, 40));
        Brush gridBrush = ThemeBrush("BorderColorBrush", Color.FromRgb(122, 128, 138));
        Brush textBrush = ThemeBrush("BorderColorLightBrush", Color.FromRgb(166, 172, 182));
        Brush accentBrush = ThemeBrush("AccentColorLighterBrush", Color.FromRgb(239, 82, 95));
        drawingContext.DrawRoundedRectangle(surfaceBrush, new Pen(gridBrush, 1), bounds, 8, 8);

        const double left = 46;
        const double top = 24;
        double right = width - 20;
        double bottom = height - 42;
        double plotWidth = Math.Max(1, right - left);
        double plotHeight = Math.Max(1, bottom - top);
        Brush subduedGridBrush = gridBrush.Clone();
        subduedGridBrush.Opacity = 0.35;
        var gridPen = new Pen(subduedGridBrush, 1);
        for (int index = 0; index <= 4; index++)
        {
            double x = left + (plotWidth * index / 4);
            double y = bottom - (plotHeight * index / 4);
            drawingContext.DrawLine(gridPen, new Point(x, top), new Point(x, bottom));
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(right, y));
        }

        double deadzoneRight = left + (plotWidth * _deadzone);
        drawingContext.PushOpacity(0.22);
        drawingContext.DrawRectangle(accentBrush, null,
            new Rect(left, top, Math.Max(0, deadzoneRight - left), plotHeight));
        drawingContext.Pop();
        drawingContext.DrawLine(new Pen(accentBrush, 1),
            new Point(deadzoneRight, top), new Point(deadzoneRight, bottom));

        Brush referenceBrush = gridBrush.Clone();
        referenceBrush.Opacity = 0.5;
        var referencePen = new Pen(referenceBrush, 1) { DashStyle = DashStyles.Dash };
        drawingContext.DrawLine(referencePen, new Point(left, bottom), new Point(right, top));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index <= 160; index++)
            {
                double input = index / 160.0;
                double normalized = input <= _deadzone ? 0 : (input - _deadzone) / (1 - _deadzone);
                double output = Math.Pow(normalized, _exponent);
                var point = new Point(left + (plotWidth * input), bottom - (plotHeight * output));
                if (index == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(accentBrush, 3), geometry);

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawText(drawingContext, "0%", left - 8, bottom + 8, dip, textBrush);
        DrawText(drawingContext, "50%", left + (plotWidth / 2) - 12, bottom + 8, dip, textBrush);
        DrawText(drawingContext, "100% input", right - 58, bottom + 8, dip, textBrush);
        DrawText(drawingContext, "100%", 5, top - 7, dip, textBrush);
        DrawText(drawingContext, "output", 7, top + 12, dip, textBrush);
        if (_deadzone > 0.001f)
            DrawText(drawingContext, $"deadzone {_deadzone * 100:0}%", left + 5, bottom - 23, dip, textBrush);
    }

    private Brush ThemeBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static void DrawText(DrawingContext context, string text, double x, double y, double pixelsPerDip, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, pixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}

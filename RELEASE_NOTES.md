# P3R Camera Fix Reload 2.0.0

Version 2 is a major camera-control rework for both mouse and gamepad.

## Before You Update

- You can update directly from version 1; uninstalling the old version is not required.
- Version 1 settings do not map directly to the new configuration. After updating, open **Configure Mod** in Reloaded-II and review the new defaults and presets.
- Reloaded-II should install the new `Reloaded.SharedLib.Hooks` dependency automatically.

## Highlights

- Added direct raw-mouse camera input without simulated analog-stick behavior.
- Added configurable gamepad speed, deadzone, response curves, smoothing, and recentering.
- Added proper control for spline and rail-camera areas while preserving their full authored view range.
- Added a dedicated configuration window with presets and a live response-curve graph.
- Improved camera and cursor behavior across dialogue, menus, battles, fades, and gameplay transitions.
- Added active XInput controller selection support.

If the camera does not initially feel right, start with mouse sensitivity or gamepad turn speed, then try the available response presets.

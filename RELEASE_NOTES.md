# P3R Camera Fix Reload 2.1.0-rc1

This release candidate improves controller compatibility and makes switching between controller and mouse more reliable.

## What's New

- Direct gamepad input no longer hooks the game's controller APIs, reducing conflicts with Steam Input and controller handling.
- Added a mutually exclusive GameInput fallback when an active controller is unavailable through XInput.
- Improved controller disconnect, reconnect, and native-input fallback behavior.
- Restores raw mouse input when P3R drops its registration, preventing controller-to-mouse handoff from getting stuck.

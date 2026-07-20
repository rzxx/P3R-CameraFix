# Persona 3 Reload Camera Fix

A Reloaded-II mod that replaces Persona 3 Reload's delayed, stick-like camera input with responsive mouse and gamepad controls.

## What it fixes

- Uses physical raw mouse movement for same-frame camera rotation instead of P3R's center-warped mouse-to-stick path.
- Reads the right stick before the game's large upstream deadzone, then applies a small configurable radial deadzone and response curve.
- Removes the free camera's acceleration, deceleration, and input delays while retaining native collision, pitch limits, and camera-follow behavior.
- Replaces the spline/rail camera's 166.7 ms input interpolator and rigid 20-degrees-per-second follower.
- Makes mouse angles persistent in spline cameras, with a predictable recenter only during authored rail motion or native input locks.
- Preserves native dialogue, menu, loading, fade, and cursor ownership across camera and input-device transitions.

P3R has separate free, spline/rail, and literal fixed field-camera implementations. This mod changes the free and spline paths. Literal fixed cameras remain native.

## Installation

1. Download the latest package from [Releases](https://github.com/rzxx/P3R-CameraFix/releases).
2. Drag the zip file onto Reloaded-II.
3. Enable **P3R Camera Fix**.
4. Launch Persona 3 Reload through Reloaded-II.

## Configuration

Select **P3R Camera Fix** in Reloaded-II and press **Configure Mod**. This opens a dedicated camera panel instead of Reloaded's generic property grid. The window follows the active Reloaded theme, while boolean settings use conventional pill switches with explicit labels and descriptions:

| Setting | Default | Purpose |
| --- | ---: | --- |
| Mouse Horizontal / Vertical Sensitivity | `100%` | Free-camera mouse base; exact integer sliders avoid noisy float values |
| Gamepad Horizontal Speed | `165°/s` | Maximum free-camera yaw speed at full stick |
| Gamepad Vertical Speed | `100°/s` | Maximum free-camera pitch speed at full stick |
| Gamepad Deadzone | `3%` | Shared radial deadzone, adjustable from 0–50% |
| Camera Response Curve | `Standard` | Standard, Comfort, Direct, Dynamic, or Custom stick-to-camera response |
| Spline Mouse / Gamepad Sensitivity Multiplier | `100%` | Relative to each free-camera base |
| Invert Mouse Y | Off | Shared mouse inversion for both camera types |

The response graph shows how right-stick travel becomes camera turn demand in both camera implementations and updates live while configuring it. **Standard** is the recommended set-and-forget curve; **Comfort** makes small adjustments calmer while retaining fast full-stick turns. **Direct (Linear)** exposes a transparent proportional response, while **Dynamic (S-Curve)** keeps the center calm and accelerates through medium/large movement. Choosing **Custom** adds an exponent slider from 1.00 to 3.00, plus independent **Low-End Calm (Toe)** and **High-End Reach (Shoulder)** controls. Toe keeps small camera corrections lower without expanding the deadzone; shoulder pulls large camera movement toward full speed sooner. Both preserve exact zero/full-stick endpoints and a monotonic response.

The **Advanced** tab exposes input-source toggles, spline smoothing and recenter behavior, and native camera parameters. Cursor ownership, cursor-warp rejection, and raw-input recovery use the validated implementation directly rather than presenting internal correctness controls as user settings.

The **Debug** tab is only for diagnosing a reproducible problem. While a camera trace is active, pressing **Page Up** writes an immediately flushed marker whose timestamp can be matched across the trace files. All traces are disabled by default; with debugging off, trace buffers, marker files, timers, cursor polling, and per-frame telemetry records are not created.

Most numerical settings update while the game is running. Settings described as restart-required install or remove native hooks and therefore take effect on the next launch.

## Default gamepad response

The default radial camera response is a mild conventional power curve: it keeps small corrections gentle and still reaches full speed at full stick, without temporal acceleration or hidden segmented boosts. Reloaded's configuration button opens a dedicated camera panel with four curated camera-feel presets, Custom endpoint shaping, and a live graph of the exact resulting curve. Maximum horizontal free-camera speed defaults to 165 degrees per second, and the separate hardware deadzone defaults to 3%, suitable for precise Hall-effect sticks while still tolerating a small amount of ordinary stick noise.

## How it works

The mod signature-scans the supported executable for the native free-camera update, spline interpolator, field-camera operation tick, and final view-transform path. Raw mouse deltas are applied at the native pitch/yaw result sites. Direct controller demand enters before P3R's upstream remap. Native camera ownership, collision, authored rail movement, fades, message UI, and common actor-based UI remain authoritative.

The original behavior-object patch is applied only when a free-camera behavior is created or its settings change. Cached behavior liveness is checked periodically without managed allocations. Camera hooks do no trace construction or file I/O when debugging is disabled.

**Target:** Persona 3 Reload (Steam/Windows), Unreal Engine 4.27.2, module `xrd777`

## Building from source

Requirements:

- .NET 8 SDK or later
- Reloaded-II
- Persona 3 Reload (Steam/Windows)

```text
git clone https://github.com/rzxx/P3R-CameraFix
dotnet build -c Release
```

## Credits

- [p3rpc.nativetypes](https://github.com/rirurin/p3rpc.nativetypes) by Rirurin
- [p3rpc.essentials](https://github.com/AnimatedSwine37/p3rpc.essentials) by AnimatedSwine37
- [p5r-freecam](https://github.com/rirurin/p5r-freecam) by Rirurin
- [UnrealEssentials](https://github.com/AnimatedSwine37/UnrealEssentials) by AnimatedSwine37
- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS)
- [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II)

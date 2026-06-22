# Persona 3 Reload Camera Fix

A Reloaded-II mod that removes the sluggish acceleration and smoothing from P3R's normal third-person camera.

P3R has two camera systems: fixed and normal third-person. This mod only affects the third-person one. Fixed cameras are left alone - they feel fine as-is.

## Features

- Removes gamepad camera smoothing/acceleration on the normal third-person camera
- Camera responds instantly to stick input
- Independent tunable values for yaw (horizontal) and pitch (vertical) camera movement
- Configurable speed, acceleration, deceleration, and input delay parameters
- All settings adjustable at runtime via Config.json

## Installation

1. Download the latest release from [Releases](https://github.com/rzxx/P3R-CameraFix/releases).
2. Drag and drop the release zip file onto the Reloaded-II window.
3. Enable **P3R Camera Fix** in the Reloaded-II mod list.
4. Launch Persona 3 Reload through Reloaded-II (do not launch directly through Steam).

## Configuration

Edit `Config.json` inside the mod's folder to adjust camera behavior. The following parameters are available:

| Parameter                | Default | Description                                                                  |
| ------------------------ | ------- | ---------------------------------------------------------------------------- |
| `YawSpeed`               | 125.0   | Horizontal camera rotation speed                                             |
| `YawAcceleration`        | 0.0     | Time to reach full speed (seconds, 0 = instant)                              |
| `YawDeceleration`        | 0.0     | Time to stop from full speed (seconds, 0 = instant)                          |
| `YawPress`               | 0.0     | Delay before horizontal rotation starts (seconds)                            |
| `YawRelease`             | 0.0     | Delay before horizontal deceleration kicks in (seconds)                      |
| `PitchSpeed`             | 90.0    | Vertical camera rotation speed                                               |
| `PitchAcceleration`      | 0.0     | Time to reach full speed (seconds, 0 = instant)                              |
| `PitchDeceleration`      | 0.0     | Time to stop from full speed (seconds, 0 = instant)                          |
| `PitchPress`             | 0.0     | Delay before vertical rotation starts (seconds)                              |
| `PitchRelease`           | 0.0     | Delay before vertical deceleration kicks in (seconds)                        |
| `CorrectionSpeed`        | 35.0    | Auto-correction rotation speed                                               |
| `CorrectionAcceleration` | 0.5     | Auto-correction accel time (seconds, keep non-zero for smooth camera-follow) |
| `CorrectionDeceleration` | 0.3     | Auto-correction decel time (seconds)                                         |
| `CorrectionPress`        | 0.3     | Auto-correction press delay (seconds)                                        |
| `CorrectionRelease`      | 0.0     | Auto-correction release delay (seconds)                                      |

Values are applied live when Config.json changes.

## How It Works

The mod signature-scans Unreal Engine's object globals and
`StaticConstructObject_Internal`. A native construction hook observes camera
behavior creation and resolves the `FldCameraBehaviorFree` class pointer
lazily.

UE object construction is re-entrant, so the hook does not modify an object
immediately when an inner construction call returns. It deduplicates camera
behavior pointers per thread and writes the configured `YawParam`,
`PitchParam`, and `CorrectionParam` values only after the outermost construction
call has completed. This prevents partially initialized camera state from
consuming modified values.

There is no recurring timer, map polling, or per-frame callback. Normal
steady-state gameplay executes no mod code. Editing Config.json triggers one
explicit scan so existing camera objects are updated immediately.

**Target:** Persona 3 Reload (Steam/Windows), Unreal Engine 4.27.2, module `xrd777`

If a game update changes a required signature, the mod logs an error and does
not install the construction hook.

## Building from Source

**Requirements:**

- .NET 8.0 SDK or later
- Reloaded-II mod loader installed
- Persona 3 Reload (Steam/Windows)

**Steps:**

1. Clone the repository:
   ```
   git clone https://github.com/rzxx/P3R-CameraFix
   ```
2. Open the solution in Visual Studio 2022 or build via command line:
   ```
   dotnet build
   ```
3. The compiled mod will be placed in the `publish` folder.

**Dependencies:**

- Reloaded.Memory.SigScan.ReloadedII (included with Reloaded-II)
- Reloaded Shared Library: Hooks

## Credits

- [p3rpc.nativetypes](https://github.com/rirurin/p3rpc.nativetypes) by Rirurin - signature patterns
- [p3rpc.essentials](https://github.com/AnimatedSwine37/p3rpc.essentials) by AnimatedSwine37 - mod template
- [p5r-freecam](https://github.com/rirurin/p5r-freecam) by Rirurin - camera struct research
- [UnrealEssentials](https://github.com/AnimatedSwine37/UnrealEssentials) by AnimatedSwine37 - UE4 modding framework
- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) - Lua scripting and object dumping
- [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) - mod loader framework

# Persona 3 Reload Camera Fix

A Reloaded-II mod that fixes the overworld camera smoothing and acceleration for gamepad, making camera movement feel responsive like Persona 5 Royal. Removes the sluggish camera acceleration curve and replaces it with immediate, precise control.

## Features

- Removes gamepad camera smoothing/acceleration in the overworld
- Camera responds instantly to stick input, matching Persona 5 Royal feel
- Independent tunable values for yaw (horizontal) and pitch (vertical) camera movement
- Configurable speed, acceleration, deceleration, and input delay parameters
- All settings adjustable at runtime via Config.json
- No permanent game file modifications. Runs entirely through Reloaded-II

## Installation

1. Download the latest release from [Releases](https://github.com/rzxx/P3R-CameraFix/releases).
2. Drag and drop the release zip file onto the Reloaded-II window.
3. Enable **P3R Camera Fix** in the Reloaded-II mod list.
4. Launch Persona 3 Reload through Reloaded-II (do not launch directly through Steam).

## Configuration

Edit `Config.json` inside the mod's folder to adjust camera behavior. The following parameters are available:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `YawSpeed` | 125.0 | Horizontal camera rotation speed |
| `YawAcceleration` | 0.0 | Time to reach full speed (seconds, 0 = instant) |
| `YawDeceleration` | 0.0 | Time to stop from full speed (seconds, 0 = instant) |
| `YawPress` | 0.0 | Delay before horizontal rotation starts (seconds) |
| `YawRelease` | 0.0 | Delay before horizontal deceleration kicks in (seconds) |
| `PitchSpeed` | 90.0 | Vertical camera rotation speed |
| `PitchAcceleration` | 0.0 | Time to reach full speed (seconds, 0 = instant) |
| `PitchDeceleration` | 0.0 | Time to stop from full speed (seconds, 0 = instant) |
| `PitchPress` | 0.0 | Delay before vertical rotation starts (seconds) |
| `PitchRelease` | 0.0 | Delay before vertical deceleration kicks in (seconds) |
| `CorrectionSpeed` | 35.0 | Auto-correction rotation speed |
| `CorrectionAcceleration` | 0.5 | Auto-correction accel time (seconds, keep non-zero for smooth camera-follow) |
| `CorrectionDeceleration` | 0.3 | Auto-correction decel time (seconds) |
| `CorrectionPress` | 0.3 | Auto-correction press delay (seconds) |
| `CorrectionRelease` | 0.0 | Auto-correction release delay (seconds) |

Values are applied live. Changes to Config.json take effect on the next scan cycle (every 1 second by default).

## How It Works

The mod uses signature scanning to locate `FUObjectArray` and `FNamePool` in the P3R executable, which provide access to all active Unreal Engine objects. A timer runs at a configurable interval to search for `FldCameraBehaviorFree` objects, the classes responsible for overworld camera movement. When found, the mod writes the configured values directly into each object's `YawParam`, `PitchParam`, and `CorrectionParam` fields, overriding the game's default acceleration curve.

**Target:** Persona 3 Reload (Steam/Windows), Unreal Engine 4.27.2, module `xrd777`

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
   dotnet build p3rpc.camfix/p3rpc.camfix.csproj
   ```
3. The compiled mod will be placed in the `Publish` folder. See Reloaded-II documentation for packaging details.

**Dependencies:**
- Reloaded.Memory.SigScan.ReloadedII (included with Reloaded-II)

## Credits

- [p3rpc.nativetypes](https://github.com/rirurin/p3rpc.nativetypes) by Rirurin - signature patterns
- [p3rpc.essentials](https://github.com/AnimatedSwine37/p3rpc.essentials) by AnimatedSwine37 - mod template
- [p5r-freecam](https://github.com/rirurin/p5r-freecam) by Rirurin - camera struct research
- [UnrealEssentials](https://github.com/AnimatedSwine37/UnrealEssentials) by AnimatedSwine37 - UE4 modding framework
- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) - Lua scripting and object dumping
- [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) - mod loader framework

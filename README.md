# P3R Camera Fix Reload

**P3R Camera Fix Reload** is a comprehensive rework of Persona 3 Reload’s camera controls for both mouse and gamepad. It replaces the port’s awkward input handling with a system designed around each input method, while working closely with the game’s movement, UI, dialogue, and transitions.

The mod comes with tuned defaults, so you can install it and start playing. If the camera still does not feel right for you, a dedicated settings panel gives you clear presets and detailed control over its behavior.

## Features

**Direct mouse and gamepad controls.** Mouse movement is handled as real mouse input instead of a simulated analog stick. Both input methods reach the camera immediately, without the original sluggish acceleration, deceleration, input delays, or heavy smoothing. The mod works both during normal gameplay and in Tartarus.

**Designed to feel native.** The rework stays integrated with P3R’s original camera and game states, so collision, character following, and recentering continue to work as intended. It also prevents the mouse cursor from appearing when you don’t need it.

**Extensive customization.** The dedicated settings window lets you adjust mouse sensitivity, gamepad speed, independent X/Y inversion for each input method, deadzone, response curves, smoothing, recentering, and native camera-follow behavior. It includes ready-made presets and a live graph showing exactly how the selected gamepad curve responds.

## Installation

1. Download the latest package from [Releases](https://github.com/rzxx/P3R-CameraFix/releases).
2. Drag the downloaded `.7z` package onto Reloaded-II.
3. Enable **P3R Camera Fix Reload**.
4. Launch Persona 3 Reload through Reloaded-II.

## Configuration

Select **P3R Camera Fix Reload** in Reloaded-II and press **Configure Mod**.

The defaults are tuned for immediate play. If something feels off, adjust mouse sensitivity or gamepad turn speed first, then try a different response preset if needed.

| Setting                                 |         Default | Purpose                                                        |
| --------------------------------------- | --------------: | -------------------------------------------------------------- |
| Mouse Horizontal / Vertical Sensitivity |          `100%` | Main mouse sensitivity                                         |
| Gamepad Horizontal Speed                | `165 degrees/s` | Maximum horizontal turn speed                                  |
| Gamepad Vertical Speed                  | `100 degrees/s` | Maximum vertical turn speed                                    |
| Gamepad Deadzone                        |            `8%` | Adjust to prevent unwanted camera movement from stick drift    |
| Camera Response Curve                   |      `Standard` | How right-stick travel becomes camera rotation                 |
| Spline Mouse Sensitivity Multiplier     |           `50%` | Mouse sensitivity in constrained camera areas                  |
| Spline Gamepad Turn-Speed Multiplier    |           `25%` | Constrained-camera turn-rate limit; preserves full angle reach |
| Invert Mouse X / Y                      |       Off / Off | Reverses either mouse camera axis independently                |
| Invert Gamepad X / Y                    |       Off / Off | Reverses either right-stick camera axis independently          |

Gamepad response presets:

- **Standard (Recommended):** balanced for general play.
- **Comfort:** reduces camera movement from small and medium stick input.
- **Direct (Linear):** maps stick position directly to camera speed.
- **Dynamic (S-Curve):** increases camera movement from medium and large stick input.
- **Custom:** lets you shape the low, middle, and high parts of the response yourself.

On spline/rail cameras, stick deflection still selects an angle within the authored range, while the spline gamepad multiplier controls how quickly the camera can reach it. Lowering the multiplier calms the camera without shrinking the available view range; returning the stick to center still recenters the controller offset.

The mod's inversion controls apply to its direct raw-mouse and direct gamepad paths. If either direct input source is disabled—or the spline camera temporarily uses P3R's native mouse-axis recovery—the native axis is left unchanged and P3R's own inversion setting remains authoritative.

The **Advanced** tab contains input-source toggles, turn-demand smoothing and recenter controls, and the game’s native camera-follow parameters. Most numerical changes apply while the game is running; settings marked as restart-required take effect on the next launch.

## Technical notes

- Startup signature scans resolve the native free-camera update, spline interpolator, field-camera operation tick, and final view-transform path for the supported executable.
- Relative mouse `WM_INPUT` counts are bucketed per camera frame and applied at the native pitch/yaw result sites; controller HID packets are ignored by this path.
- Right-stick state is polled without detouring the game's controller APIs. XInput is preferred when active, GameInput is selected as a mutually exclusive fallback for native HID gamepads, and P3R's native axes remain the final fallback.
- The spline replacement separates user offset from authored rail motion. Native input locks and rail motion feed its configurable recenter state instead of discarding the stored angle.
- Native fade, message, actor-UI, field-operation, and battle-command state arbitrate cursor ownership across gameplay transitions.
- The original camera behavior patch runs when its object is created or its settings change. Debug tracing performs no buffer allocation or file I/O while disabled.

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

To create the same Reloaded-II release assets used by CI, run:

```powershell
./Publish.ps1 -Version 2.0.0
```

The package and its required update metadata are written to `Publish/ToUpload`.

## Credits

- [p3rpc.nativetypes](https://github.com/rirurin/p3rpc.nativetypes) by Rirurin
- [p3rpc.essentials](https://github.com/AnimatedSwine37/p3rpc.essentials) by AnimatedSwine37
- [p5r-freecam](https://github.com/rirurin/p5r-freecam) by Rirurin
- [P3RFix](https://codeberg.org/Lyall/P3RFix) by Lyall
- [UnrealEssentials](https://github.com/AnimatedSwine37/UnrealEssentials) by AnimatedSwine37
- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS)
- [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II)

# Next Steps: Tier 2 / Tier 3 — Eliminating Polling Entirely

Tier 1 (the current implementation) eliminates the 1-second FPS drops by replacing
the periodic full-scan timer with a cheap liveness check. But it still *polls* —
every 15s it checks if the cached behavior pointers are still valid, and on map
load it does a full UObject scan to find the new behaviors.

Tiers 2 and 3 eliminate polling entirely by hooking native functions so the mod
is notified exactly when a camera behavior is created or initialized. The values
are written once, in the hook, and no timer is needed at all.

## Tier 2: Vtable Hook (medium effort, no Ghidra needed)

### Idea

`UFldCameraBehaviorFree` is a `UObject` (specifically a `UActorComponent`).
Its vtable pointer is at offset `0x0` of the object. The vtable contains
pointers to all the virtual methods: `PostInitProperties`, `InitializeComponent`,
`TickComponent`, `BeginDestroy`, etc.

Instead of scanning the UObject array, we:
1. Find one `UFldCameraBehaviorFree` instance (Tier 1 already does this).
2. Read its vtable pointer.
3. Find the `InitializeComponent` (or `PostInitProperties`) slot in the vtable.
4. Hook that function pointer using `IReloadedHooks.CreateHook`.
5. In the hook, after the original runs, write our values to `this`.

### How to find the right vtable slot

We don't know the exact vtable index for `PostInitProperties` / `InitializeComponent`
in P3R's UE4.27 build. But we can find it empirically:

1. Read the vtable of a live `UFldCameraBehaviorFree` instance.
2. For each function pointer in the vtable, check if the function's code
   references the `YawParam` offset (`0x00E8`). The init function will write
   the default values to these offsets.
3. Alternatively, identify the slot by cross-referencing with a known UE4.27
   vtable layout (the `UActorComponent` vtable is well-documented in the UE
   source code).

A simpler approach: hook the CDO's class constructor. The `UClass` struct (at
`obj->ClassPrivate`) has a `ClassDefaultObject` field at offset `0x118` (from
`UnrealTypes.cs`). The CDO's vtable is the same as instance vtables. We can
scan the CDO's vtable for a function that writes `0.1f` to offset `0x00EC`
(the default `YawParam.Acceleration` value from the dump).

### Implementation sketch

```csharp
// After Tier 1 finds a behavior:
var vtable = *(nint*)behavior;  // offset 0x0
// InitializeComponent is typically at a specific vtable index.
// For UE4.27 UActorComponent, try indices around 20-40.
// Read the function pointer and hook it.
var initFnAddr = *(nint*)(vtable + vtableIndex * 8);
_initHook = _hooks.CreateHook<InitDelegate>(InitHook, initFnAddr).Activate();

// In the hook:
private static void InitHook(UnrealTypes.UObject* self)
{
    _initHook.OriginalFunction(self);
    // After the original init writes default values, overwrite them.
    ApplyValuesToBehavior(self);
}
```

### What you need to do

1. Add `Reloaded.SharedLib.Hooks` NuGet dependency to `p3rpc.camfix.csproj`.
2. Get `IReloadedHooks` from the mod loader (see `p3rpc.essentials` ModContext.cs).
3. After Tier 1 finds the first behavior, read its vtable and dump the first
   ~50 function pointers. Log them so you can identify which one is the init
   function (the one that writes to offset `0x00E8`).
4. Hook that function and write values in the hook.
5. Remove the timer entirely.

### Reference code

- `p3rpc.essentials/Patches/IntroSkip.cs` — shows the `IReloadedHooks` pattern:
  sigscan → `hooks.CreateHook<Delegate>(HookFunc, address).Activate()` →
  call `_hook.OriginalFunction(args)` → modify state.
- `p3rpc.essentials/Utilities/WndProcHook.cs` — shows hooking a function
  pointer obtained at runtime (not via sigscan).

---

## Tier 3: Full RE Init-Function Hook (hardest, zero overhead)

### Idea

Find the native C++ function that initializes `UFldCameraBehaviorFree`'s
`YawParam` / `PitchParam` / `CorrectionParam` fields with their default values.
This is either:
- The C++ constructor (`UFldCameraBehaviorFree::UFldCameraBehaviorFree`)
- `PostInitProperties`
- `InitializeComponent`
- A custom Atlus init function called during field map setup

Sigscan for this function directly, hook it, and write our values after the
original runs. This is the true O(1) solution: zero scanning, zero polling,
runs only when a behavior is actually created.

### How to find the sig

This requires Ghidra or IDA analysis of `xrd777.exe`:

1. **String reference approach**: Search the binary for the string
   `"FldCameraBehaviorFree"`. The class registration code references this
   string. Near the registration, you'll find the constructor and
   `PostInitProperties` function pointers.

2. **Offset reference approach**: Search for instructions that write to
   offset `0x00E8` on a register that could be `this` (e.g.,
   `mov [rcx+0xE8], eax` where `eax` holds a float like `0.1f`).
   The function containing these writes is the init function.

3. **Vtable approach** (same as Tier 2): Find the vtable for
   `UFldCameraBehaviorFree` in the binary, read the `PostInitProperties` slot,
   and use that function's bytes as the sig.

### Implementation sketch

```csharp
// In Mod constructor, after globals sigscan:
Utils.SigScan("XX XX XX XX ... (init function sig)", "CameraBehaviorInit", address =>
{
    _initHook = _hooks.CreateHook<InitDelegate>(CameraBehaviorInit, address).Activate();
});

private static void CameraBehaviorInit(UnrealTypes.UObject* self)
{
    _initHook.OriginalFunction(self);
    if (IsCameraBehaviorClassFast(self))
        ApplyValuesToBehavior(self);
}
```

### What you need to do

1. Load `xrd777.exe` in Ghidra (or the full P3R exe).
2. Find the `UFldCameraBehaviorFree` class registration (search for the string).
3. Identify the constructor / `PostInitProperties` / `InitializeComponent`.
4. Extract a unique byte signature for the function prologue.
5. Add the sig to the mod, hook it, write values in the hook.
6. Remove the timer and the UObject scanning code entirely.

### Reference repos

- `p5r-freecam/p5r-freecam/src/hooks/field.rs` — shows sigscan hooking of
  native field camera functions (P5R uses the same Atlus engine family).
  The sig `"40 53 48 83 EC 50 48 8B 59 ?? 0F 29 74 24 ?? 0F 28 F1"` hooks
  `fldPCMoveUpdate`. P3R's equivalent will be different (UE4 vs native GFD
  engine) but the approach is the same.
- `p3rpc.essentials/Patches/IntroSkip.cs` — shows the C# hook pattern.
- `p3re_ghidra_scripts/` — existing Ghidra scripts for P3R (currently just
  enum generators; could be extended with a camera-behavior-finder script).

---

## Research workspace

The `C:\Users\pufok\P3R-Modding` workspace contains all the research that
informed this mod:

- `workspace/findings.md` — P3R camera architecture, confirmed dead ends
- `workspace/P3RCamParams.txt` — full dump of `UFldCameraBehaviorFree` fields
- `workspace/P3RCamDump.txt` — full dump of `AFldOperator` and camera objects
- `workspace/P3RCamFix.log` — UE4SS UFunction probe results (0 fired)
- `references/p3rpc.essentials/` — C# hook pattern (IntroSkip.cs)
- `references/p3rpc.nativetypes/` — P3R struct definitions and sig patterns
- `references/p5r-freecam/` — P5R camera hooking (Rust, same engine family)
- `SESSION_HANDOFF.md` — full research handoff document

### Key findings (don't retry these dead ends)

- UE4SS Lua hooks: 3388 UFunctions scanned, 0 fired during gameplay. P3R's
  camera+input is entirely native C++.
- `AddYawInput` / `AddPitchInput` / `AddRollInput`: register but never fire.
  P3R's `KernelInput` bypasses UE input.
- `ReceiveTick` on `FldCamera`: never fires. Camera driven by native tick.
- `SmoothTargetViewRotationSpeed` (offset `0x02E4`): set to 10000, barely
  noticeable. Real smoothing is in `YawParam` / `PitchParam` /
  `CorrectionParam` on the behavior component.

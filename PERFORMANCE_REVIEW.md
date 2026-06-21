# P3R-CameraFix Performance Review

## TL;DR

The 1-second FPS drops you're seeing are **almost certainly caused by this mod**. The timer interval at `Mod.cs:119` is `1000ms`, which matches your "drops every second" symptom exactly. The tick does far more work than it needs to, allocates strings on every iteration, and re-scans the entire UObject array (~100k+ objects) on cache invalidation.

The good news: the core approach (sigscan globals, write into behavior structs) is sound. The bad news: the implementation does an order of magnitude more work than necessary on the hot path.

---

## How the mod currently works

1. On load: sigscans `FUObjectArray` and `FNamePool` globals.
2. Starts a `System.Threading.Timer` firing every **1000ms** (`Mod.cs:119`).
3. Each tick (`ApplyFixTick`):
   - If cache is non-empty, calls `AreBehaviorsValid()` — which calls `GetClassName()` for **every** cached behavior, **allocating a managed string each time** via `Marshal.PtrToStringUni/Ansi`.
   - If all valid, calls `ApplyValuesToBehavior()` on each — which reads 3 floats, compares, and only writes if changed.
   - If any invalid, clears cache and calls `FindCameraBehaviors()` — a **full scan of every UObject in every chunk**, calling `GetClassName()` on each (another string alloc per object), wrapped in `try/catch`.

---

## Issues, ranked by impact

### P0 — Root cause of the 1-second hitch

#### 1. `Marshal.PtrToStringUni/Ansi` on the hot path (`UnrealTypes.cs:79-81`)

Every call to `GetClassName()` allocates a **new managed `string`** through `Marshal.PtrToString*`. This is one of the slowest ways to read a string from native memory: it copies bytes, decodes, allocates a `System.String` object, and hands it to the GC.

Where it's called from:
- `FindCameraBehaviors()` — once per UObject in the global array (potentially **100,000+ times** per full scan).
- `AreBehaviorsValid()` → `IsBehaviorValid()` — once per cached behavior, **every tick** (every 1s).
- `ApplyValuesToBehavior()` logging path (only on write — minor).

Even on the "fast" cached path, you're still allocating strings every second per behavior. On the slow path, you're allocating tens of thousands of strings in a burst, which triggers Gen0/Gen1 GC and can stall the game's main thread for several ms.

**This alone explains the periodic drops.**

#### 2. Full UObject array scan on cache miss (`Mod.cs:169-218`)

`FindCameraBehaviors()` walks the entire `FUObjectArray` — every chunk, every slot — to find a handful of camera behaviors. With a typical P3R session the array holds on the order of 100k+ live UObjects. For each one it:
- Dereferences `chunk[i].Object`,
- Reads `ClassPrivate`,
- Calls `GetClassName()` (string alloc),
- Does a managed `string ==` comparison.

Then wraps the whole body in `try/catch` with an empty handler. If any single slot points at bad memory (which happens routinely as UE4's GC moves/destroys objects), an `AccessViolation`-derived exception is thrown and caught. **Throwing exceptions in .NET costs ~1µs each**, and during a scan where hundreds of slots are stale, this adds up to tens of milliseconds of pure exception-throwing overhead.

#### 3. `AreBehaviorsValid()` is a misdiagnosis of "validity" (`Mod.cs:159-167`)

The cache validation strategy is: "call `GetClassName` on every cached pointer every tick; if the string doesn't match our class names, invalidate the whole cache and re-scan." Problems:
- It allocates strings every tick (see P0 #1).
- A single transient memory issue invalidates the entire cache and triggers a full re-scan.
- It can't actually tell stale pointers from live ones reliably — it just checks the class name string, which may *happen* to still read correctly from a freed slot.

The proper validity check is much cheaper (see fixes below).

---

### P1 — Secondary waste

#### 4. `try/catch` around every slot in the scan loop (`Mod.cs:194-212`)

The `try { ... } catch { }` inside the inner loop means every iteration has exception-handling overhead even when nothing throws. The JIT will still emit the EH tables and the catch block is evaluated on every throw. Replacing this with a cheap memory-validity probe (or simply trusting UE4's GC marks) eliminates the throw cost entirely.

#### 5. No caching of class FNames

The class names `FldCameraBehaviorFree` and `BP_FldCameraBehaviorFree_C` are stable `FName` entries in the pool. After the first successful match, you could store the `FName.PoolLocation` (a `uint`) for each class and then do a simple `obj->ClassPrivate->baseObj.NamePrivate.PoolLocation == cachedPoolLoc` check — an **integer compare** instead of a string alloc + compare. This makes the scan ~50-100x faster.

#### 6. `Log()` interpolation always allocates (`Mod.cs:307`)

`Log($"...")` builds the interpolated string **before** the `_logger?.WriteLine` call, so even when `_logger` is null (or when you'd rather not log), the string is still allocated. In the scan path, `Log($"  MATCH: ...")` runs for every match and `Log($"Scanning {count} objects ...")` runs every full scan. Not the main issue, but contributes to GC pressure on the slow path.

#### 7. Cache is rebuilt as a new `List<IntPtr>` every invalidation (`Mod.cs:139`)

`_cachedBehaviors.Clear()` followed by `Add` in a loop is fine, but the old list's backing array becomes garbage. Over many invalidations this churns Gen1/Gen2. Minor.

---

### P2 — Design / strategy

#### 8. A 1-second polling timer is the wrong abstraction

You don't need to poll every second. The camera behaviors are created when the field map loads and persist for the whole field session. Better strategies, in increasing order of effort:

- **a. One-shot scan after level load.** Wait for `FUObjectArray.NumElements` to be non-zero, scan once, then only re-scan when the cached pointers go bad. Drop the periodic timer entirely (or move to a 10-30s "safety" re-scan).
- **b. Hook `ULevel::PostLoadMap` or similar** via Reloaded-II's function hooking. Scan once when a map loads. Zero ongoing cost.
- **c. Hook the function that initializes `ULdCameraBehaviorFree`** (the camera system's `PostInitProperties` or the C++ constructor that writes the default `YawParam`/`PitchParam`/`CorrectionParam`). Apply your values there. O(1), runs only when a behavior is actually created. This is the *correct* solution and eliminates all polling.

#### 9. Re-applying every second is also unnecessary once values stick

`ApplyValuesToBehavior` already has a `needsWrite` check (`Mod.cs:265-269`), which is good. But the game only resets these values when the behavior is (re)initialized — not continuously. Polling every 1s to "re-apply if reverted" is wasteful when you could just re-apply on the event that reverts them (map load / behavior spawn).

#### 10. Thread pool starvation risk

`System.Threading.Timer` runs callbacks on the thread pool. A scan that takes 20-50ms blocks a pool thread for that long. UE4/Reloaded may also use the pool for short tasks, and a long callback can delay them. If you keep polling, at least cap the scan cost (P0/P1 fixes) and consider a dedicated long-running task (`Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`) instead of a timer.

#### 11. No thread safety on `_cachedBehaviors`

`ConfigurationUpdated` (`Mod.cs:310-316`) clears `_cachedBehaviors` from whatever thread Reloaded calls it on, while the timer callback reads/writes it on a pool thread. Not a perf bug, but a correctness race that can cause `InvalidOperationException` ("Collection was modified") or stale reads — which would also trigger spurious full re-scans.

---

## Estimated cost of one tick (current code)

| Scenario                          | Work per tick                                | Approx cost        |
| --------------------------------- | -------------------------------------------- | ------------------ |
| Cache valid, no values to write   | N × `GetClassName` (string alloc) + N × `ApplyValuesToBehavior` read-only | ~0.1-0.5 ms (N small) + GC pressure |
| Cache valid, values differ        | above + 15 float writes + 1 interpolated log | ~0.2-0.6 ms        |
| Cache invalid → full scan         | 100k × `GetClassName` + likely many thrown AVs | **10-50 ms+**, triggers Gen0/Gen1 GC, can stall main thread |

The third row is your 30-FPS drop. Even the first row contributes a steady GC drip that can cause micro-hitches.

---

## Recommended fixes (in priority order)

### Fix A — Cache class FNames, kill the string allocs (biggest win, smallest change)

After the first successful match, store:

```csharp
private static uint _fldCameraBehaviorFreeName;
private static uint _bpFldCameraBehaviorFreeCName;
private static bool _classNamesResolved;
```

In `FindCameraBehaviors`, when you find a match, record `obj->ClassPrivate->baseObj.NamePrivate.PoolLocation`. On subsequent scans (and in `IsBehaviorValid`), compare `PoolLocation` directly:

```csharp
private static unsafe bool IsCameraBehaviorClass(UObject* obj)
{
    if (obj->ClassPrivate == null) return false;
    var poolLoc = obj->ClassPrivate->baseObj.NamePrivate.PoolLocation;
    return poolLoc == _fldCameraBehaviorFreeName || poolLoc == _bpFldCameraBehaviorFreeCName;
}
```

This turns every per-object check from a string alloc + comparison into a single `uint` compare. **Expected scan speedup: 50-100x.** Expected steady-state cost per tick: microseconds.

### Fix B — Replace `AreBehaviorsValid` with a cheap liveness check

Instead of calling `GetClassName` (string alloc), check structural validity only:

```csharp
private static unsafe bool IsBehaviorValid(UObject* obj)
{
    if (obj == null) return false;
    if ((obj->ObjectFlags & 0x10) != 0) return false; // still a CDO
    if (obj->ClassPrivate == null) return false;
    // Optional: range-check the pointer against the UObject array's address space
    return IsCameraBehaviorClass(obj); // uses cached PoolLocation (Fix A)
}
```

No string allocs on the hot path at all.

### Fix C — Drop the `try/catch` inside the scan loop

Once you're comparing integers instead of dereferencing through `Marshal`, the main source of AVs goes away. For the remaining `chunk[i].Object` dereference, either:
- Trust UE4's GC (objects in the array are valid unless explicitly destroyed), or
- Do a single `VirtualQuery` per chunk to confirm the chunk pointer is readable before iterating.

This removes the per-iteration exception-throw cost.

### Fix D — Stop the 1-second polling

Pick one:

- **Easy:** Change the timer to fire once at startup (after a 5s delay), then re-scan only when `IsBehaviorValid` fails for a cached entry. Use a *long* re-scan interval (e.g. 30s) as a safety net, not 1s.
- **Better:** Use Reloaded-II's update loop / a map-load hook to scan exactly when needed. No polling at all.
- **Best:** Hook the camera behavior's init function and write values there. O(1), no scanning, no polling, no cache. This is how camera mods for other UE4 games typically do it.

### Fix E — Don't allocate log strings unless you'll log

Either guard with a verbosity flag:

```csharp
private static bool _verbose = false;
private static void Log(string msg) { if (_verbose) _logger?.WriteLine($"[P3R CamFix] {msg}"); }
```

…or pass a `Func<string>` lazily. At minimum, remove the `Log` calls from inside the scan loop and the `ApplyValuesToBehavior` write path, or gate them behind a "first detection only" flag.

### Fix F — Thread safety

Use a `lock` around `_cachedBehaviors` mutation, or switch to an immutable snapshot pattern (replace the whole list atomically). Cheap and avoids spurious invalidations from races.

---

## After fixes — expected steady-state cost

| Scenario                          | Cost per tick    |
| --------------------------------- | ---------------- |
| Cache valid, values already set   | ~1-5 µs (a few int compares + 3 float reads) |
| Cache valid, values reverted      | ~1-10 µs + 15 float writes |
| Cache invalid (rare)              | ~0.5-2 ms full scan with int compares (no string allocs, no exceptions) |

That's a **~1000-10000x reduction** in per-tick work and eliminates the GC pressure that's almost certainly causing your 1-second hitches.

---

## What's actually fine

- The sigscan + RIP-relative resolution (`Mod.cs:81-110`) — one-time cost, correct approach.
- The struct layouts in `UnrealTypes.cs` — match UE4 4.27 layout for P3R.
- The `needsWrite` guard in `ApplyValuesToBehavior` (`Mod.cs:265-269`) — good, avoids redundant writes.
- Direct float writes via `*(float*)(addr + N)` — optimal, no marshalling overhead.
- The CDO check via `ObjectFlags & 0x10` — correct and cheap.
- The 5000ms startup delay (`Mod.cs:119`) — sensible, lets the game initialize its UObject array.

The bones are good. It's the **scan + validation strategy** and the **string allocation pattern** that need surgery.

---

## Quickest minimal patch to test the hypothesis

If you want to verify this mod is the culprit before doing the full refactor, try either of these one-line experiments:

1. **Disable the timer entirely** (comment out `Mod.cs:119`). If the drops stop, this mod is it.
2. **Bump the interval to 10000ms** (`1000` → `10000`). If the drops move to every 10s, this mod is it.
3. **Set `Enabled = false` in Config.json** (keeps the timer running but makes `ApplyFixTick` return early at `Mod.cs:125`). If drops **stop**, the work is the problem. If drops **continue**, it's the timer/timer-thread itself (unlikely but possible).

Most likely outcome of #3: drops stop → confirms the scan/validation work is the cause, not the timer machinery.

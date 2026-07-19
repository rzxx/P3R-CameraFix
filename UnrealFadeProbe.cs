using p3rpc.camfix.Template;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Read-only discovery/snapshot helper for xrd777.UISubsystem::FadePlayer_.
/// It uses the same generated UE4.27 object layout and signatures already
/// validated by P3RFix, but does not invoke ProcessEvent or mutate an object.
/// </summary>
internal sealed unsafe class UnrealFadeProbe
{
    private const string GObjectsSignature =
        "48 8B 05 ?? ?? ?? ?? 48 8B 0C ?? 48 8D 04 ?? 48 85 C0 74 ?? 44 39 40 ?? 75 ?? F7 40 ?? 00 00 00 30 75 ?? 48 8B 00";

    private const string FNameAppendStringSignature =
        "48 89 ?? ?? ?? E8 ?? ?? ?? ?? 48 8B ?? ?? 48 85 ?? 75 ?? 48 8B ?? ?? ?? 48 8B ??";

    private const int ObjectItemSize = 0x18;
    private const int ObjectsPerChunk = 0x10000;
    private const int FadeWordCount = 24; // UFadePlayer +0x28 through +0x84.
    private const int UiContactWordCount = 100; // UUIContactManager +0x48 through +0x1D4.

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly nint _imageBase;
    private readonly Dictionary<nint, string> _classNames = new();
    private nint _gObjects;
    private nint _appendString;
    private nint _uiSubsystem;
    private nint _fadePlayer;
    private int _scanIndex = -1;
    private int _messageScanIndex = -1;
    private long _nextScanQpc;
    private long _nextMessageScanQpc;
    private readonly List<nint> _messageManagers = new();
    private readonly List<nint> _fieldManagers = new();
    private readonly List<nint> _townMapActors = new();
    private readonly List<nint> _uiContactManagers = new();
    private bool _invalidObjectsLogged;
    private bool _foundLogged;
    private bool _messageFoundLogged;
    private bool _fieldManagerFoundLogged;
    private bool _townMapFoundLogged;
    private bool _uiContactFoundLogged;

    public UnrealFadeProbe(ModContext context, nint imageBase)
    {
        _logger = context.Logger;
        _imageBase = imageBase;

        context.StartupScanner.AddMainModuleScan(GObjectsSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Fade probe: FUObjectArray signature not found.", System.Drawing.Color.Orange);
                return;
            }

            nint instruction = _imageBase + result.Offset;
            nint address = instruction + 7 + *(int*)(instruction + 3);
            Volatile.Write(ref _gObjects, address);
            _logger.WriteLine($"[P3R CamFix] Fade probe: FUObjectArray resolved at P3R.exe+0x{(address - _imageBase):X}.");
        });

        context.StartupScanner.AddMainModuleScan(FNameAppendStringSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Fade probe: FName::AppendString signature not found.", System.Drawing.Color.Orange);
                return;
            }

            nint call = _imageBase + result.Offset + 5;
            nint target = call + 5 + *(int*)(call + 1);
            Volatile.Write(ref _appendString, target);
            _logger.WriteLine($"[P3R CamFix] Fade probe: FName::AppendString resolved at P3R.exe+0x{(target - _imageBase):X}.");
        });
    }

    public FadeSnapshot Capture()
    {
        TryDiscover();
        DiscoverMessageManagers();

        var result = new FadeSnapshot
        {
            Status = _fadePlayer != 0 ? 2 : _gObjects != 0 && _appendString != 0 ? 1 : 0,
            UiSubsystem = _uiSubsystem,
            FadePlayer = _fadePlayer,
        };

        nint fade = _fadePlayer;
        if (fade != 0)
        {
            for (int index = 0; index < FadeWordCount; index++)
                result.SetWord(index, *(uint*)(fade + 0x28 + index * 4));

            result.Programs = *(nint*)(fade + 0x88);
            result.ProgramCount = *(int*)(fade + 0x90);
            result.ProgramMax = *(int*)(fade + 0x94);
        }

        CaptureMessageState(ref result);
        CaptureTownMapState(ref result);
        CaptureUiContactState(ref result);
        return result;
    }

    /// <summary>
    /// Reads only the already-discovered FadePlayer mode. Unlike Capture this
    /// does not scan the object array or call any UE function, so the cursor
    /// hook can use the native fade lifecycle even while field-operation ticks
    /// are stalled or have stopped during a return to the title screen.
    /// </summary>
    public bool TryReadLiveMode(out int mode)
    {
        mode = 0;
        nint fade = Volatile.Read(ref _fadePlayer);
        if (fade == 0)
            return false;

        uint value = *(uint*)(fade + 0x30);
        if (value > 2)
            return false;

        mode = unchecked((int)value);
        return true;
    }

    public bool TryReadLiveMessageCursorOwner(out bool active)
    {
        active = false;
        bool available = false;
        int candidateCount = _messageManagers.Count;
        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            nint manager = _messageManagers[candidateIndex];
            nint procList = *(nint*)(manager + 0x38);
            int procCount = *(int*)(manager + 0x40);
            int procMax = *(int*)(manager + 0x44);
            if (procCount is < 0 or > 1024 || procMax < procCount || procMax > 4096)
                continue;

            available = true;
            for (int procIndex = 0; procIndex < procCount; procIndex++)
            {
                nint proc = procList == 0 ? 0 : *(nint*)(procList + procIndex * sizeof(nint));
                if (proc != 0 && (*(nint*)(proc + 0x48) != 0 || *(nint*)(proc + 0x50) != 0))
                {
                    active = true;
                    return true;
                }
            }
        }
        return available;
    }

    /// <summary>
    /// Reads P3R's common actor-based UI ownership state. UIContactManager keeps
    /// a persistent registry, so list presence is not activity. Runtime controls
    /// show that every live registered actor flips AActor::bHidden together while
    /// an actor-based interface owns input (ordinary menu, town map, exit UI),
    /// and returns together for field gameplay and loading holds.
    /// </summary>
    public bool TryReadLiveActorUiCursorOwner(out bool active)
    {
        active = false;
        bool available = false;
        int candidateCount = _uiContactManagers.Count;
        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            nint manager = _uiContactManagers[candidateIndex];
            nint actorList = *(nint*)(manager + 0x38);
            int actorCount = *(int*)(manager + 0x40);
            int actorMax = *(int*)(manager + 0x44);
            if (actorCount is < 0 or > 4096 || actorMax < actorCount || actorMax > 16384)
                continue;

            available = true;
            int nonNullCount = 0;
            int hiddenCount = 0;
            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                nint actor = actorList == 0 ? 0 : *(nint*)(actorList + actorIndex * sizeof(nint));
                if (actor == 0 || (*(uint*)(actor + 0x5C) & (1u << 4)) != 0)
                    continue;

                nonNullCount++;
                if ((*(uint*)(actor + 0x58) & (1u << 5)) != 0)
                    hiddenCount++;
            }

            if (nonNullCount >= 2 && hiddenCount == nonNullCount)
            {
                active = true;
                return true;
            }
        }
        return available;
    }

    private void TryDiscover()
    {
        if (_fadePlayer != 0)
        {
            nint fadeClass = *(nint*)(_fadePlayer + 0x10);
            if (fadeClass != 0 && GetClassName(fadeClass) == "FadePlayer")
                return;

            _fadePlayer = 0;
            _uiSubsystem = 0;
            _scanIndex = -1;
        }

        nint objects = Volatile.Read(ref _gObjects);
        nint append = Volatile.Read(ref _appendString);
        if (objects == 0 || append == 0)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextScanQpc)
            return;

        nint chunkTable = *(nint*)objects;
        int count = *(int*)(objects + 0x14);
        int chunkCount = *(int*)(objects + 0x1C);
        if (chunkTable == 0 || count is <= 0 or > 5_000_000 || chunkCount is <= 0 or > 256)
        {
            if (!_invalidObjectsLogged)
            {
                _invalidObjectsLogged = true;
                _logger.WriteLine($"[P3R CamFix] Fade probe: implausible FUObjectArray (count={count}, chunks={chunkCount}).", System.Drawing.Color.Orange);
            }
            _nextScanQpc = now + System.Diagnostics.Stopwatch.Frequency;
            return;
        }

        if (_scanIndex < 0 || _scanIndex >= count)
            _scanIndex = count - 1;

        // Spread name resolution over frames to avoid a discovery hitch.
        int remaining = 2048;
        while (_scanIndex >= 0 && remaining-- > 0)
        {
            nint instance = GetObject(chunkTable, chunkCount, _scanIndex--);
            if (instance == 0)
                continue;

            nint instanceClass = *(nint*)(instance + 0x10);
            if (instanceClass == 0 || GetClassName(instanceClass) != "UISubsystem")
                continue;

            nint fade = *(nint*)(instance + 0x60);
            if (fade == 0)
                continue;

            nint fadeClass = *(nint*)(fade + 0x10);
            if (fadeClass == 0 || GetClassName(fadeClass) != "FadePlayer")
                continue;

            _uiSubsystem = instance;
            _fadePlayer = fade;
            if (!_foundLogged)
            {
                _foundLogged = true;
                _logger.WriteLine($"[P3R CamFix] Fade probe active: UISubsystem=0x{instance:X}, FadePlayer=0x{fade:X}.");
            }
            return;
        }

        if (_scanIndex < 0)
        {
            _scanIndex = -1;
            _nextScanQpc = now + System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void DiscoverMessageManagers()
    {
        nint objects = Volatile.Read(ref _gObjects);
        nint append = Volatile.Read(ref _appendString);
        if (objects == 0 || append == 0)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextMessageScanQpc)
            return;

        nint chunkTable = *(nint*)objects;
        int count = *(int*)(objects + 0x14);
        int chunkCount = *(int*)(objects + 0x1C);
        if (chunkTable == 0 || count is <= 0 or > 5_000_000 || chunkCount is <= 0 or > 256)
        {
            _nextMessageScanQpc = now + System.Diagnostics.Stopwatch.Frequency;
            return;
        }

        if (_messageScanIndex < 0 || _messageScanIndex >= count)
            _messageScanIndex = count - 1;

        int remaining = 2048;
        while (_messageScanIndex >= 0 && remaining-- > 0)
        {
            nint instance = GetObject(chunkTable, chunkCount, _messageScanIndex--);
            if (instance == 0)
                continue;

            nint instanceClass = *(nint*)(instance + 0x10);
            if (instanceClass == 0)
                continue;

            string className = GetClassName(instanceClass);
            if (className == "MsgManager")
            {
                if (!_messageManagers.Contains(instance))
                    _messageManagers.Add(instance);
                if (!_messageFoundLogged)
                {
                    _messageFoundLogged = true;
                    _logger.WriteLine($"[P3R CamFix] Message probe active: MsgManager=0x{instance:X}.");
                }
            }
            else if (className == "FldManagerSubsystem")
            {
                if (!_fieldManagers.Contains(instance))
                    _fieldManagers.Add(instance);
                if (!_fieldManagerFoundLogged)
                {
                    _fieldManagerFoundLogged = true;
                    _logger.WriteLine($"[P3R CamFix] Town-map probe: FldManagerSubsystem=0x{instance:X}.");
                }
            }
            else if (className == "UITownMapActor")
            {
                if (!_townMapActors.Contains(instance))
                    _townMapActors.Add(instance);
                if (!_townMapFoundLogged)
                {
                    _townMapFoundLogged = true;
                    _logger.WriteLine($"[P3R CamFix] Town-map probe: UITownMapActor candidate=0x{instance:X}.");
                }
            }
            else if (className == "UIContactManager")
            {
                if (!_uiContactManagers.Contains(instance))
                    _uiContactManagers.Add(instance);
                if (!_uiContactFoundLogged)
                {
                    _uiContactFoundLogged = true;
                    _logger.WriteLine($"[P3R CamFix] General UI probe: UIContactManager=0x{instance:X}.");
                }
            }
        }

        if (_messageScanIndex < 0)
        {
            _messageScanIndex = -1;
            _nextMessageScanQpc = now + System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void CaptureMessageState(ref FadeSnapshot result)
    {
        result.MessageManagerCandidateCount = _messageManagers.Count;
        foreach (nint manager in _messageManagers)
        {
            nint managerClass = *(nint*)(manager + 0x10);
            if (managerClass == 0 || GetClassName(managerClass) != "MsgManager")
                continue;

            nint procList = *(nint*)(manager + 0x38);
            int procCount = *(int*)(manager + 0x40);
            int procMax = *(int*)(manager + 0x44);
            if (procCount is < 0 or > 1024 || procMax < procCount || procMax > 4096)
                continue;

            if (procCount > 0)
                result.ActiveMessageManagerCount++;

            for (int procIndex = 0; procIndex < procCount; procIndex++)
            {
                nint proc = procList == 0 ? 0 : *(nint*)(procList + procIndex * sizeof(nint));
                if (proc == 0)
                    continue;
                if (*(nint*)(proc + 0x48) != 0)
                    result.ActiveMessageItemCount++;
                if (*(nint*)(proc + 0x50) != 0)
                    result.ActiveSelectionItemCount++;
            }

            if (result.MessageManager != 0 && procCount <= result.MessageProcCount)
                continue;

            result.MessageManager = manager;
            result.MessageProcList = procList;
            result.MessageProcCount = procCount;
            result.MessageProcMax = procMax;
            result.MessageReleaseList = *(nint*)(manager + 0x50);
            result.MessageReleaseCount = *(int*)(manager + 0x58);
            result.MessageReleaseMax = *(int*)(manager + 0x5C);

            nint firstProc = procList != 0 && procCount > 0 ? *(nint*)procList : 0;
            result.MessageFirstProc = firstProc;
            result.MessageFirstItem = firstProc == 0 ? 0 : *(nint*)(firstProc + 0x48);
            result.MessageFirstSelect = firstProc == 0 ? 0 : *(nint*)(firstProc + 0x50);
        }

        result.MessageProbeStatus = result.MessageManager != 0 ? 2 :
            Volatile.Read(ref _gObjects) != 0 && Volatile.Read(ref _appendString) != 0 ? 1 : 0;
    }

    private void CaptureTownMapState(ref FadeSnapshot result)
    {
        result.FieldManagerCandidateCount = _fieldManagers.Count;
        result.TownMapActorCandidateCount = _townMapActors.Count;

        foreach (nint manager in _fieldManagers)
        {
            nint managerClass = *(nint*)(manager + 0x10);
            if (managerClass == 0 || GetClassName(managerClass) != "FldManagerSubsystem")
                continue;

            nint largeMapActor = *(nint*)(manager + 0x158);
            bool classMatches = IsClass(largeMapActor, "UITownMapActor");
            if (result.FieldManager == 0 || largeMapActor != 0)
            {
                result.FieldManager = manager;
                result.FieldOperator = *(nint*)(manager + 0x100);
                result.LargeMapActor = largeMapActor;
                result.LargeMapActorClassMatches = classMatches ? 1 : 0;
            }
            if (largeMapActor != 0)
                result.ActiveLargeMapManagerCount++;
        }

        // GObjects retains actor candidates beyond their useful gameplay lifetime.
        // Prefer a non-destroying actor with a populated town-map-specific pointer;
        // record raw AActor flags as well so the runtime trace can establish which
        // native lifetime signal actually brackets the interactive map.
        int bestScore = int.MinValue;
        foreach (nint actor in _townMapActors)
        {
            if (!IsClass(actor, "UITownMapActor"))
                continue;

            uint flags58 = *(uint*)(actor + 0x58);
            uint flags5C = *(uint*)(actor + 0x5C);
            bool hidden = (flags58 & (1u << 5)) != 0;
            bool destroying = (flags5C & (1u << 4)) != 0;
            nint input = *(nint*)(actor + 0xF8);
            nint root = *(nint*)(actor + 0x130);
            nint locationSelect = *(nint*)(actor + 0x480);
            nint fieldCamera = *(nint*)(actor + 0x4E8);
            nint mainCamera = *(nint*)(actor + 0x4F0);
            nint startCamera = *(nint*)(actor + 0x4F8);

            int score = (destroying ? -100 : 0) + (hidden ? -10 : 10) +
                (root != 0 ? 20 : 0) + (locationSelect != 0 ? 40 : 0) +
                (fieldCamera != 0 || mainCamera != 0 || startCamera != 0 ? 30 : 0);
            if (!destroying && !hidden && root != 0)
                result.LiveTownMapActorCount++;
            if (score <= bestScore)
                continue;

            bestScore = score;
            result.TownMapActor = actor;
            result.TownMapActorFlags58 = flags58;
            result.TownMapActorFlags5C = flags5C;
            result.TownMapActorInput = input;
            result.TownMapActorRoot = root;
            result.TownMapLocationSelect = locationSelect;
            result.TownMapFieldCamera = fieldCamera;
            result.TownMapMainCamera = mainCamera;
            result.TownMapStartCamera = startCamera;
        }

        result.TownMapProbeStatus = result.FieldManager != 0 || result.TownMapActor != 0 ? 2 :
            Volatile.Read(ref _gObjects) != 0 && Volatile.Read(ref _appendString) != 0 ? 1 : 0;
    }

    private bool IsClass(nint instance, string expectedName)
    {
        if (instance == 0)
            return false;
        nint instanceClass = *(nint*)(instance + 0x10);
        return instanceClass != 0 && GetClassName(instanceClass) == expectedName;
    }

    private void CaptureUiContactState(ref FadeSnapshot result)
    {
        result.UiContactManagerCandidateCount = _uiContactManagers.Count;
        foreach (nint manager in _uiContactManagers)
        {
            if (!IsClass(manager, "UIContactManager"))
                continue;

            nint actorList = *(nint*)(manager + 0x38);
            int actorCount = *(int*)(manager + 0x40);
            int actorMax = *(int*)(manager + 0x44);
            if (actorCount is < 0 or > 4096 || actorMax < actorCount || actorMax > 16384)
                continue;

            if (result.UiContactManager != 0 && actorCount <= result.UiContactActorCount)
                continue;

            result.UiContactManager = manager;
            result.UiContactActorList = actorList;
            result.UiContactActorCount = actorCount;
            result.UiContactActorMax = actorMax;

            for (int wordIndex = 0; wordIndex < UiContactWordCount; wordIndex++)
                result.SetUiContactWord(wordIndex, *(uint*)(manager + 0x48 + wordIndex * 4));

            ulong hash = 14695981039346656037UL;
            for (int index = 0; index < actorCount; index++)
            {
                nint actor = actorList == 0 ? 0 : *(nint*)(actorList + index * sizeof(nint));
                hash = (hash ^ unchecked((ulong)actor)) * 1099511628211UL;
                if (actor == 0)
                    continue;

                result.UiContactNonNullActorCount++;
                nint actorClass = *(nint*)(actor + 0x10);
                nint input = *(nint*)(actor + 0xF8);
                nint root = *(nint*)(actor + 0x130);
                uint flags58 = *(uint*)(actor + 0x58);
                uint flags5C = *(uint*)(actor + 0x5C);
                hash = (hash ^ unchecked((ulong)actorClass)) * 1099511628211UL;
                hash = (hash ^ unchecked((ulong)input)) * 1099511628211UL;
                hash = (hash ^ unchecked((ulong)root)) * 1099511628211UL;
                hash = (hash ^ flags58) * 1099511628211UL;
                hash = (hash ^ flags5C) * 1099511628211UL;
                if ((flags58 & (1u << 5)) != 0)
                    result.UiContactHiddenActorCount++;
                if ((flags5C & (1u << 4)) != 0)
                    result.UiContactDestroyingActorCount++;
                if (input != 0)
                    result.UiContactInputActorCount++;
                if (root != 0)
                    result.UiContactRootActorCount++;
                result.SetUiContactActor(index, actor, actorClass, input, root, flags58, flags5C);
            }
            result.UiContactActorHash = hash;
        }

        result.UiContactProbeStatus = result.UiContactManager != 0 ? 2 :
            Volatile.Read(ref _gObjects) != 0 && Volatile.Read(ref _appendString) != 0 ? 1 : 0;
    }

    private string GetClassName(nint classObject)
    {
        if (_classNames.TryGetValue(classObject, out string? result))
            return result;

        result = ReadName(classObject);
        _classNames[classObject] = result;
        return result;
    }

    private string ReadName(nint unrealObject)
    {
        nint appendAddress = Volatile.Read(ref _appendString);
        if (unrealObject == 0 || appendAddress == 0)
            return string.Empty;

        char* buffer = stackalloc char[256];
        buffer[0] = '\0';
        FString value = new()
        {
            Data = (nint)buffer,
            Count = 0,
            Max = 256,
        };

        var append = (delegate* unmanaged<nint, nint, void>)appendAddress;
        append(unrealObject + 0x18, (nint)(&value));
        int length = Math.Clamp(value.Count > 0 ? value.Count - 1 : 0, 0, 255);
        return new string(buffer, 0, length);
    }

    private static nint GetObject(nint chunkTable, int chunkCount, int index)
    {
        int chunkIndex = index / ObjectsPerChunk;
        if ((uint)chunkIndex >= (uint)chunkCount)
            return 0;
        nint chunk = *(nint*)(chunkTable + chunkIndex * sizeof(nint));
        if (chunk == 0)
            return 0;
        return *(nint*)(chunk + (index % ObjectsPerChunk) * ObjectItemSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FString
    {
        public nint Data;
        public int Count;
        public int Max;
    }
}

internal unsafe struct FadeSnapshot
{
    public const int WordCount = 24;
    public const int UiContactWordCount = 100;
    public int Status;
    public nint UiSubsystem;
    public nint FadePlayer;
    public nint Programs;
    public int ProgramCount;
    public int ProgramMax;
    public int MessageProbeStatus;
    public int MessageManagerCandidateCount;
    public int ActiveMessageManagerCount;
    public int ActiveMessageItemCount;
    public int ActiveSelectionItemCount;
    public nint MessageManager;
    public nint MessageProcList;
    public int MessageProcCount;
    public int MessageProcMax;
    public nint MessageReleaseList;
    public int MessageReleaseCount;
    public int MessageReleaseMax;
    public nint MessageFirstProc;
    public nint MessageFirstItem;
    public nint MessageFirstSelect;
    public int TownMapProbeStatus;
    public int FieldManagerCandidateCount;
    public int ActiveLargeMapManagerCount;
    public nint FieldManager;
    public nint FieldOperator;
    public nint LargeMapActor;
    public int LargeMapActorClassMatches;
    public int TownMapActorCandidateCount;
    public int LiveTownMapActorCount;
    public nint TownMapActor;
    public uint TownMapActorFlags58;
    public uint TownMapActorFlags5C;
    public nint TownMapActorInput;
    public nint TownMapActorRoot;
    public nint TownMapLocationSelect;
    public nint TownMapFieldCamera;
    public nint TownMapMainCamera;
    public nint TownMapStartCamera;
    public int UiContactProbeStatus;
    public int UiContactManagerCandidateCount;
    public nint UiContactManager;
    public nint UiContactActorList;
    public int UiContactActorCount;
    public int UiContactActorMax;
    public int UiContactNonNullActorCount;
    public int UiContactHiddenActorCount;
    public int UiContactDestroyingActorCount;
    public int UiContactInputActorCount;
    public int UiContactRootActorCount;
    public ulong UiContactActorHash;
    private fixed ulong _uiContactActors[8];
    private fixed ulong _uiContactActorClasses[8];
    private fixed ulong _uiContactActorInputs[8];
    private fixed ulong _uiContactActorRoots[8];
    private fixed uint _uiContactActorFlags58[8];
    private fixed uint _uiContactActorFlags5C[8];
    private fixed uint _uiContactWords[UiContactWordCount];
    private fixed uint _words[WordCount];

    // Runtime-validated UFadePlayer mode at +0x30: 0=idle, 1/2=active phases.
    public uint Mode => GetWord(2);

    public void SetWord(int index, uint value)
    {
        if ((uint)index >= WordCount)
            return;
        fixed (uint* words = _words)
            words[index] = value;
    }

    public uint GetWord(int index)
    {
        if ((uint)index >= WordCount)
            return 0;
        fixed (uint* words = _words)
            return words[index];
    }

    public void SetUiContactActor(int index, nint actor, nint actorClass, nint input, nint root, uint flags58, uint flags5C)
    {
        if ((uint)index >= 8)
            return;
        fixed (ulong* actors = _uiContactActors)
        fixed (ulong* classes = _uiContactActorClasses)
        fixed (ulong* inputs = _uiContactActorInputs)
        fixed (ulong* roots = _uiContactActorRoots)
        fixed (uint* actorFlags58 = _uiContactActorFlags58)
        fixed (uint* actorFlags5C = _uiContactActorFlags5C)
        {
            actors[index] = unchecked((ulong)actor);
            classes[index] = unchecked((ulong)actorClass);
            inputs[index] = unchecked((ulong)input);
            roots[index] = unchecked((ulong)root);
            actorFlags58[index] = flags58;
            actorFlags5C[index] = flags5C;
        }
    }

    public nint GetUiContactActor(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (ulong* values = _uiContactActors) return unchecked((nint)values[index]);
    }

    public nint GetUiContactActorClass(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (ulong* values = _uiContactActorClasses) return unchecked((nint)values[index]);
    }

    public nint GetUiContactActorInput(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (ulong* values = _uiContactActorInputs) return unchecked((nint)values[index]);
    }

    public nint GetUiContactActorRoot(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (ulong* values = _uiContactActorRoots) return unchecked((nint)values[index]);
    }

    public uint GetUiContactActorFlags58(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (uint* values = _uiContactActorFlags58) return values[index];
    }

    public uint GetUiContactActorFlags5C(int index)
    {
        if ((uint)index >= 8) return 0;
        fixed (uint* values = _uiContactActorFlags5C) return values[index];
    }

    public void SetUiContactWord(int index, uint value)
    {
        if ((uint)index >= UiContactWordCount) return;
        fixed (uint* values = _uiContactWords) values[index] = value;
    }

    public uint GetUiContactWord(int index)
    {
        if ((uint)index >= UiContactWordCount) return 0;
        fixed (uint* values = _uiContactWords) return values[index];
    }
}

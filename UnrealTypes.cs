namespace p3rpc.camfix;

internal static unsafe class UnrealTypes
{
    private const int ObjectsPerChunk = 0x10000;
    private const int ObjectItemSize = 0x18;
    private const int ObjectArrayObjectsOffset = 0x00;
    private const int ObjectArrayNumElementsOffset = 0x14;
    private const int ObjectArrayNumChunksOffset = 0x1C;
    private const int ObjectItemObjectOffset = 0x00;
    private const int ObjectItemFlagsOffset = 0x08;
    private const int ObjectItemSerialNumberOffset = 0x10;
    private const int UObjectFlagsOffset = 0x08;
    private const int UObjectInternalIndexOffset = 0x0C;
    private const uint InvalidObjectFlags =
        (1u << 21) | // Garbage
        (1u << 22) | // PersistentGarbage
        (1u << 28) | // Unreachable
        (1u << 29) | // PendingKill
        (1u << 31);  // PendingConstruction
    private const uint InvalidUObjectFlags =
        (1u << 15) | // BeginDestroyed
        (1u << 16) | // FinishDestroyed
        (1u << 29) | // PendingKill
        (1u << 30);  // Garbage

    /// <summary>
    /// Non-owning UObject identity. Creation accepts a live engine-owned
    /// pointer and reads its internal index once. Subsequent resolution treats
    /// the retained pointer only as an opaque identity token: the object-array
    /// slot, serial number, flags, and current pointer are validated before the
    /// resolved object is dereferenced. Encoding the index as index+1 leaves
    /// the all-zero value as null even when UE has not allocated a nonzero
    /// serial number for an object yet.
    /// </summary>
    public readonly record struct ObjectHandle(int EncodedIndex, int SerialNumber, nint Identity)
    {
        public bool IsSet => EncodedIndex != 0;
        public int Index => EncodedIndex - 1;

        public static ObjectHandle Create(int index, int serialNumber, nint identity) =>
            index < 0 || identity == 0
                ? default
                : new ObjectHandle(index + 1, serialNumber, identity);
    }

    /// <summary>
    /// Resolves an object-array item without touching a previously cached
    /// UObject pointer. The returned object is suitable for immediate use on
    /// the game thread only.
    /// </summary>
    public static bool TryResolveObject(nint objectArray, ObjectHandle handle, out nint instance)
    {
        instance = 0;
        if (!handle.IsSet || !TryGetObjectItem(objectArray, handle.Index, out nint item))
            return false;

        if (*(int*)(item + ObjectItemSerialNumberOffset) != handle.SerialNumber ||
            (*(uint*)(item + ObjectItemFlagsOffset) & InvalidObjectFlags) != 0)
            return false;

        nint current = *(nint*)(item + ObjectItemObjectOffset);
        if (current == 0 || current != handle.Identity || HasInvalidUObjectFlags(current) ||
            *(int*)(current + UObjectInternalIndexOffset) != handle.Index)
            return false;

        instance = current;
        return true;
    }

    public static bool TryGetObject(
        nint objectArray,
        int index,
        out ObjectHandle handle,
        out nint instance)
    {
        handle = default;
        instance = 0;
        if (!TryGetObjectItem(objectArray, index, out nint item) ||
            (*(uint*)(item + ObjectItemFlagsOffset) & InvalidObjectFlags) != 0)
            return false;

        nint current = *(nint*)(item + ObjectItemObjectOffset);
        if (current == 0 || HasInvalidUObjectFlags(current) ||
            *(int*)(current + UObjectInternalIndexOffset) != index)
            return false;

        handle = ObjectHandle.Create(
            index,
            *(int*)(item + ObjectItemSerialNumberOffset),
            current);
        instance = current;
        return true;
    }

    public static bool TryCreateObjectHandle(
        nint objectArray,
        nint instance,
        out ObjectHandle handle)
    {
        handle = default;
        if (instance == 0)
            return false;

        int index = *(int*)(instance + UObjectInternalIndexOffset);
        if (!TryGetObjectItem(objectArray, index, out nint item) ||
            *(nint*)(item + ObjectItemObjectOffset) != instance ||
            (*(uint*)(item + ObjectItemFlagsOffset) & InvalidObjectFlags) != 0 ||
            HasInvalidUObjectFlags(instance))
            return false;

        handle = ObjectHandle.Create(
            index,
            *(int*)(item + ObjectItemSerialNumberOffset),
            instance);
        return true;
    }

    private static bool TryGetObjectItem(nint objectArray, int index, out nint item)
    {
        item = 0;
        if (objectArray == 0 || index < 0)
            return false;

        nint chunkTable = *(nint*)(objectArray + ObjectArrayObjectsOffset);
        int count = *(int*)(objectArray + ObjectArrayNumElementsOffset);
        int chunkCount = *(int*)(objectArray + ObjectArrayNumChunksOffset);
        int chunkIndex = index / ObjectsPerChunk;
        if (chunkTable == 0 || index >= count || (uint)chunkIndex >= (uint)chunkCount)
            return false;

        nint chunk = *(nint*)(chunkTable + chunkIndex * sizeof(nint));
        if (chunk == 0)
            return false;

        item = chunk + (index % ObjectsPerChunk) * ObjectItemSize;
        return true;
    }

    private static bool HasInvalidUObjectFlags(nint instance) =>
        (*(uint*)(instance + UObjectFlagsOffset) & InvalidUObjectFlags) != 0;
}

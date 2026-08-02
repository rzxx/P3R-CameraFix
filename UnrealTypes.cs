using System.Runtime.InteropServices;
using System.Diagnostics;

namespace p3rpc.camfix;

internal static unsafe class UnrealTypes
{
    private const int ObjectsPerChunk = 0x10000;
    private const int ObjectItemSize = 0x18;
    private const uint InvalidObjectFlags =
        (uint)(EInternalObjectFlags.Garbage |
               EInternalObjectFlags.PersistentGarbage |
               EInternalObjectFlags.Unreachable |
               EInternalObjectFlags.PendingKill |
               EInternalObjectFlags.PendingConstruction);
    private const uint InvalidUObjectFlags =
        (1u << 15) | // BeginDestroyed
        (1u << 16) | // FinishDestroyed
        (1u << 29) | // PendingKill
        (1u << 30);  // Garbage

    [StructLayout(LayoutKind.Sequential, Size = 0x28)]
    public struct UObject
    {
        public IntPtr VTable;
        public uint ObjectFlags;
        public uint InternalIndex;
        public UClass* ClassPrivate;
        public FName NamePrivate;
        public UObject* OuterPrivate;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x460)]
    public struct UClass
    {
        [FieldOffset(0x0)] public UObject baseObj;
        [FieldOffset(0x118)] public UObject* ClassDefaultObject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FName
    {
        public uint PoolLocation;
        public uint Field04;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    public struct FUObjectArray
    {
        [FieldOffset(0x0)] public int ObjFirstGCIndex;
        [FieldOffset(0x4)] public int ObjLastNonGCIndex;
        [FieldOffset(0x10)] public FUObjectItem** Objects;
        [FieldOffset(0x24)] public int NumElements;
        [FieldOffset(0x2c)] public int NumChunks;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    public struct FUObjectItem
    {
        [FieldOffset(0x0)] public UObject* Object;
        [FieldOffset(0x8)] public EInternalObjectFlags Flags;
        [FieldOffset(0xC)] public int ClusterRootIndex;
        [FieldOffset(0x10)] public int SerialNumber;
    }

    [Flags]
    public enum EInternalObjectFlags : uint
    {
        None = 0,
        Garbage = 1u << 21,
        PersistentGarbage = 1u << 22,
        ReachableInCluster = 1u << 23,
        ClusterRoot = 1u << 24,
        Native = 1u << 25,
        Async = 1u << 26,
        AsyncLoading = 1u << 27,
        Unreachable = 1u << 28,
        PendingKill = 1u << 29,
        RootSet = 1u << 30,
        PendingConstruction = 1u << 31,
    }

    /// <summary>
    /// Non-owning UObject identity. The pointer is retained only as an opaque
    /// identity token and is never dereferenced before the object-array slot,
    /// serial number, flags, and current pointer have all been validated.
    /// Encoding the index as index+1 leaves the all-zero value as null even
    /// when UE has not allocated a nonzero serial number for an object yet.
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

        if (*(int*)(item + 0x10) != handle.SerialNumber ||
            (*(uint*)(item + 0x08) & InvalidObjectFlags) != 0)
            return false;

        nint current = *(nint*)item;
        if (current == 0 || current != handle.Identity || HasInvalidUObjectFlags(current) ||
            *(int*)(current + 0x0C) != handle.Index)
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
            (*(uint*)(item + 0x08) & InvalidObjectFlags) != 0)
            return false;

        nint current = *(nint*)item;
        if (current == 0 || HasInvalidUObjectFlags(current) ||
            *(int*)(current + 0x0C) != index)
            return false;

        handle = ObjectHandle.Create(index, *(int*)(item + 0x10), current);
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

        int index = *(int*)(instance + 0x0C);
        if (!TryGetObjectItem(objectArray, index, out nint item) ||
            *(nint*)item != instance ||
            (*(uint*)(item + 0x08) & InvalidObjectFlags) != 0 ||
            HasInvalidUObjectFlags(instance))
            return false;

        handle = ObjectHandle.Create(index, *(int*)(item + 0x10), instance);
        return true;
    }

    private static bool TryGetObjectItem(nint objectArray, int index, out nint item)
    {
        item = 0;
        if (objectArray == 0 || index < 0)
            return false;

        nint chunkTable = *(nint*)objectArray;
        int count = *(int*)(objectArray + 0x14);
        int chunkCount = *(int*)(objectArray + 0x1C);
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
        (*(uint*)(instance + 0x08) & InvalidUObjectFlags) != 0;

    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    public struct FNamePool
    {
        [FieldOffset(0x8)] public uint PoolCount;
        [FieldOffset(0xc)] public uint NameCount;

        public IntPtr GetPool(uint poolIdx)
        {
            fixed (FNamePool* self = &this)
                return *((IntPtr*)(self + 1) + poolIdx);
        }

        public string GetString(FName name) => GetString(name.PoolLocation);

        public string GetString(uint poolLoc)
        {
            fixed (FNamePool* self = &this)
            {
                IntPtr ptr = GetPool(poolLoc >> 0x10);
                ptr += (nint)((poolLoc & 0xFFFF) * 2);
                return GetStringFromPtr(ptr);
            }
        }

        private static string GetStringFromPtr(IntPtr ptr)
        {
            short flags = *(short*)ptr;
            int length = flags >> 6;
            bool isWide = (flags & 1) != 0;
            IntPtr strPtr = ptr + 2;
            return isWide
                ? Marshal.PtrToStringUni(strPtr, length) ?? ""
                : Marshal.PtrToStringAnsi(strPtr, length) ?? "";
        }
    }
}

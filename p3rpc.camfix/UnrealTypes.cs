using System.Runtime.InteropServices;
using System.Diagnostics;

namespace p3rpc.camfix;

internal static unsafe class UnrealTypes
{
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
    }

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

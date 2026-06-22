using System.Runtime.InteropServices;

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

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    public struct FStaticConstructObjectParameters
    {
        [FieldOffset(0x0)] public UClass* Class;
        [FieldOffset(0x8)] public UObject* Outer;
        [FieldOffset(0x10)] public FName Name;
        [FieldOffset(0x18)] public uint SetFlags;
        [FieldOffset(0x1c)] public uint InternalSetFlags;
        [FieldOffset(0x20)] public byte CopyTransientsFromClassDefaults;
        [FieldOffset(0x21)] public byte AssumeTemplateIsArchetype;
        [FieldOffset(0x28)] public UObject* Template;
        [FieldOffset(0x30)] public IntPtr InstanceGraph;
        [FieldOffset(0x38)] public IntPtr ExternalPackage;
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

        public bool EqualsAnsi(FName name, ReadOnlySpan<byte> expected)
        {
            IntPtr ptr = GetPool(name.PoolLocation >> 0x10);
            if (ptr == IntPtr.Zero) return false;

            ptr += (nint)((name.PoolLocation & 0xFFFF) * 2);
            short flags = *(short*)ptr;
            int length = flags >> 6;
            bool isWide = (flags & 1) != 0;
            if (isWide || length != expected.Length) return false;

            byte* chars = (byte*)(ptr + 2);
            for (int i = 0; i < expected.Length; i++)
            {
                if (chars[i] != expected[i]) return false;
            }

            return true;
        }

    }
}

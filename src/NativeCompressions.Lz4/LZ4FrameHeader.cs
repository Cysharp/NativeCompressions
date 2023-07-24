using System.Runtime.InteropServices;

namespace NativeCompressions.Lz4
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LZ4FrameHeader
    {
        public LZ4FrameInfo FrameInfo;
        public int CompressionLevel;
        public uint AutoFlush;
        public uint FavorDecSpeed;
        private fixed uint reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LZ4FrameInfo
    {
        public int BlockSizeID;
        public int BlockMode;
        public int ContentChecksumFlag;
        public int FrameType;
        public ulong ContentSize;
        public uint DictID;
        public int BlockChecksumFlag;
    }
}
